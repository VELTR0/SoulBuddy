using SoulBuddy.Data;
using SoulBuddy.Models;
using SoulBuddy.Sources;

namespace SoulBuddy.Services;

public sealed class SyncService
{
    private readonly AppConfig _config;
    private readonly IPartySource _partySource;
    private readonly SoullockeClient _soullockeClient;
    private readonly KnownPokemonStore _knownPokemon;
    private readonly LocationMapper _locationMapper;
    private readonly HashSet<string> _dryRunProcessedPokemonIds = [];
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SyncService(
        IPartySource partySource,
        KnownPokemonStore knownPokemon,
        SoullockeClient soullockeClient,
        LocationMapper locationMapper,
        AppConfig config)
    {
        _partySource = partySource;
        _knownPokemon = knownPokemon;
        _soullockeClient = soullockeClient;
        _locationMapper = locationMapper;
        _config = config;
    }

    /// <summary>
    /// Initializes the local SoulBuddy state. In Soullocke mode this method
    /// completes the full Soullocke-to-SoulBuddy import before the live
    /// SoulBuddy-to-Soullocke synchronization is allowed to start.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            await _knownPokemon.LoadAsync(cancellationToken);

            if (_config.SoullockeEnabled)
            {
                Console.WriteLine("Soullocke-Startimport läuft …");
                await ImportSoullockeRunAsync(cancellationToken);
                Console.WriteLine("Soullocke-Startimport abgeschlossen. Upload-Synchronisierung wird freigegeben.");
            }

            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);

        var locallyStoredPokemon =
            await _knownPokemon.GetAllAsync(cancellationToken);

        Console.WriteLine("SoulSync läuft.");
        Console.WriteLine($"party.json: {_config.PartyJsonPath}");
        Console.WriteLine(
            $"Lokal gespeicherte Pokémon: {locallyStoredPokemon.Count}");
        Console.WriteLine(
            _config.SoullockeEnabled
                ? $"Soullocke: aktiviert ({_config.PlayerId})"
                : "Soullocke: deaktiviert – Pokémon werden nur lokal gespeichert.");

        if (_config.SoullockeEnabled)
        {
            Console.WriteLine($"DryRun: {_config.DryRun}");
        }

        Console.WriteLine("Zum Beenden Strg+C drücken.");
        Console.WriteLine();

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SynchronizeOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] Fehler: {ex.Message}");
            }

            await Task.Delay(
                _config.PollIntervalMilliseconds,
                cancellationToken);
        }
    }

    private async Task ImportSoullockeRunAsync(
        CancellationToken cancellationToken)
    {
        var run = await _soullockeClient.LoadRunAsync(cancellationToken);
        var importedCount = 0;
        var refreshedCount = 0;

        foreach (var pair in run.Encounters)
        {
            var location = pair.Key;
            var encounter = pair.Value;
            if (encounter.Pokemon <= 0)
            {
                continue;
            }

            var uniqueId = $"soullocke:{_config.PlayerId}:{location}";
            var currentHp = string.Equals(
                encounter.Status,
                "fainted",
                StringComparison.OrdinalIgnoreCase)
                    ? 0
                    : 1;

            if (!_knownPokemon.Contains(uniqueId))
            {
                await _knownPokemon.AddAsync(
                    uniqueId,
                    new KnownPokemonEntry
                    {
                        UniqueId = uniqueId,
                        SpeciesId = encounter.Pokemon,
                        Species = $"Pokémon #{encounter.Pokemon}",
                        Nickname = encounter.Nickname,
                        Location = location,
                        CurrentHp = currentHp,
                        MaxHp = 1
                    },
                    cancellationToken);
                importedCount++;
            }
            else
            {
                // Soullocke is authoritative during initialization. Refresh
                // the status of entries that were imported on an earlier run.
                await _knownPokemon.UpdateCurrentStateAsync(
                    uniqueId,
                    currentLevel: 0,
                    currentHp,
                    maxHp: 1,
                    cancellationToken);
                refreshedCount++;
            }

            await _knownPokemon.MarkSoullockeSyncedAsync(
                uniqueId,
                cancellationToken);
        }

        Console.WriteLine(
            $"Soullocke-Initialisierung: {run.Encounters.Count} Begegnungen gelesen, " +
            $"{importedCount} neu importiert, {refreshedCount} aktualisiert.");
    }

    private async Task SynchronizeOnceAsync(
        CancellationToken cancellationToken)
    {
        if (!_initialized)
        {
            throw new InvalidOperationException(
                "Der Soullocke-Startimport wurde noch nicht abgeschlossen.");
        }

        var pokemonSlots =
            await _partySource.ReadAllPokemonAsync(cancellationToken);

        foreach (var slot in pokemonSlots)
        {
            var pokemon = slot.Pokemon;

            if (pokemon is null || pokemon.IsEgg || pokemon.Species <= 0)
            {
                continue;
            }

            var uniqueId = CreateUniqueId(pokemon);
            var isKnownLocally = _knownPokemon.Contains(uniqueId);

            var mappedLocationName =
                _locationMapper.GetLocationName(pokemon.LocationMet);
            var localLocationName = mappedLocationName ??
                                    $"Unbekannter Fangort ({pokemon.LocationMet})";

            if (!isKnownLocally)
            {
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] Neues Pokémon erkannt: " +
                    $"{pokemon.Nickname} ({pokemon.SpeciesName}), " +
                    $"PID {pokemon.Pid}, Fangort-ID {pokemon.LocationMet}");

                await _knownPokemon.AddAsync(
                    uniqueId,
                    new KnownPokemonEntry
                    {
                        UniqueId = uniqueId,
                        SpeciesId = pokemon.Species,
                        Species = pokemon.SpeciesName,
                        Nickname = pokemon.Nickname,
                        Pid = pokemon.Pid,
                        OriginalTrainerId = pokemon.OriginalTrainerId,
                        OriginalTrainerSecretId =
                            pokemon.OriginalTrainerSecretId,
                        Location = localLocationName,
                        LocationId = pokemon.LocationMet,
                        LevelMet = pokemon.LevelMet,
                        CurrentLevel = pokemon.Level,
                        CurrentHp = pokemon.Hp.Current,
                        MaxHp = pokemon.Hp.Max,
                        IsEgg = pokemon.IsEgg
                    },
                    cancellationToken);

                Console.WriteLine("  Lokal in soulbuddy.db gespeichert.");
            }
            else
            {
                await _knownPokemon.UpdateCurrentStateAsync(
                    uniqueId,
                    pokemon.Level,
                    pokemon.Hp.Current,
                    pokemon.Hp.Max,
                    cancellationToken);
            }

            if (!_config.SoullockeEnabled)
            {
                continue;
            }

            if (_knownPokemon.IsSoullockeSynced(uniqueId) ||
                _dryRunProcessedPokemonIds.Contains(uniqueId))
            {
                continue;
            }

            if (mappedLocationName is null)
            {
                Console.WriteLine(
                    $"  Soullocke-Synchronisierung ausstehend: " +
                    $"Fangort-ID {pokemon.LocationMet} ist noch nicht zugeordnet.");
                continue;
            }

            var run = await _soullockeClient.LoadRunAsync(cancellationToken);

            if (run.Encounters.ContainsKey(mappedLocationName))
            {
                Console.WriteLine(
                    $"  Nicht importiert: {mappedLocationName} ist bereits belegt.");

                await _knownPokemon.MarkSoullockeSyncedAsync(
                    uniqueId,
                    cancellationToken);
                continue;
            }

            var encounter = new SoullockeEncounter
            {
                Pokemon = pokemon.Species,
                Nickname = string.IsNullOrWhiteSpace(pokemon.Nickname)
                    ? null
                    : pokemon.Nickname,
                Status = pokemon.Hp.Current <= 0
                    ? "fainted"
                    : "alive"
            };

            if (_config.DryRun)
            {
                Console.WriteLine(
                    $"  DRY RUN: Würde {pokemon.SpeciesName} unter " +
                    $"„{mappedLocationName}“ mit Status " +
                    $"„{encounter.Status}“ eintragen.");
                _dryRunProcessedPokemonIds.Add(uniqueId);
                continue;
            }

            run.Encounters[mappedLocationName] = encounter;

            await _soullockeClient.SaveRunAsync(
                run.Encounters,
                cancellationToken);

            await _knownPokemon.MarkSoullockeSyncedAsync(
                uniqueId,
                cancellationToken);

            Console.WriteLine(
                $"  Erfolgreich unter „{mappedLocationName}“ eingetragen.");
        }
    }

    private static string CreateUniqueId(PartyPokemon pokemon)
    {
        return $"{pokemon.Pid}:{pokemon.OriginalTrainerId}:" +
               $"{pokemon.OriginalTrainerSecretId}";
    }
}
