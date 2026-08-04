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
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private bool _initialized;

    public SyncService(IPartySource partySource, KnownPokemonStore knownPokemon,
        SoullockeClient soullockeClient, LocationMapper locationMapper, AppConfig config)
    {
        _partySource = partySource;
        _knownPokemon = knownPokemon;
        _soullockeClient = soullockeClient;
        _locationMapper = locationMapper;
        _config = config;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            await _knownPokemon.LoadAsync(cancellationToken);
            if (_config.SoullockeEnabled)
            {
                Console.WriteLine("Soullocke-Startimport läuft …");
                await ImportSoullockeRunAsync(cancellationToken);
                Console.WriteLine("Soullocke-Startimport abgeschlossen. Upload-Synchronisierung wird freigegeben.");
            }
            _initialized = true;
        }
        finally { _initializationLock.Release(); }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        Console.WriteLine("SoulSync läuft.");
        while (!cancellationToken.IsCancellationRequested)
        {
            try { await SynchronizeOnceAsync(cancellationToken); }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { break; }
            catch (Exception ex) { Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] Fehler: {ex.Message}"); }
            await Task.Delay(_config.PollIntervalMilliseconds, cancellationToken);
        }
    }

    private async Task ImportSoullockeRunAsync(CancellationToken cancellationToken)
    {
        var run = await _soullockeClient.LoadRunAsync(cancellationToken);
        await PullRemoteEncountersAsync(run, cancellationToken);
        Console.WriteLine($"Soullocke-Initialisierung: {run.Encounters.Count} Begegnungen mit Status gelesen.");
    }

    private async Task PullRemoteEncountersAsync(SoullockeRun run, CancellationToken cancellationToken)
    {
        foreach (var pair in run.Encounters)
        {
            if (pair.Value.Pokemon <= 0) continue;
            await _knownPokemon.UpsertSoullockeEncounterAsync(
                $"soullocke:{_config.PlayerId}:{pair.Key}",
                pair.Value.Pokemon,
                pair.Value.Nickname,
                pair.Key,
                NormalizeStatus(pair.Value.Status),
                cancellationToken);
        }
    }

    private async Task SynchronizeOnceAsync(CancellationToken cancellationToken)
    {
        if (!_initialized) throw new InvalidOperationException("Der Soullocke-Startimport wurde noch nicht abgeschlossen.");
        var slots = await _partySource.ReadAllPokemonAsync(cancellationToken);
        SoullockeRun? run = _config.SoullockeEnabled ? await _soullockeClient.LoadRunAsync(cancellationToken) : null;
        if (run is not null) await PullRemoteEncountersAsync(run, cancellationToken);

        var runChanged = false;
        var faintedLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var slot in slots)
        {
            var pokemon = slot.Pokemon;
            if (pokemon is null || pokemon.IsEgg || pokemon.Species <= 0) continue;
            var mappedLocation = _locationMapper.GetLocationName(pokemon.LocationMet);
            var location = mappedLocation ?? $"Unbekannter Fangort ({pokemon.LocationMet})";
            var gameEntry = new KnownPokemonEntry
            {
                UniqueId = CreateUniqueId(pokemon), SpeciesId = pokemon.Species, Species = pokemon.SpeciesName,
                Nickname = pokemon.Nickname, Pid = pokemon.Pid, OriginalTrainerId = pokemon.OriginalTrainerId,
                OriginalTrainerSecretId = pokemon.OriginalTrainerSecretId, Location = location,
                LocationId = pokemon.LocationMet, LevelMet = pokemon.LevelMet, CurrentLevel = pokemon.Level,
                CurrentHp = pokemon.Hp.Current, MaxHp = pokemon.Hp.Max, IsEgg = pokemon.IsEgg
            };
            var mergedId = await _knownPokemon.MergeGamePokemonAsync(
                gameEntry.UniqueId, gameEntry, slot.Box is not null, cancellationToken);

            if (run is null || mappedLocation is null) continue;
            if (!run.Encounters.TryGetValue(mappedLocation, out var encounter))
            {
                encounter = new SoullockeEncounter();
                run.Encounters[mappedLocation] = encounter;
            }

            var oldStatus = NormalizeStatus(encounter.Status);
            var gameStatus = pokemon.Hp.Current <= 0 ? "fainted" : slot.Box is not null ? "boxed" : "alive";
            var newStatus = oldStatus is "brofailed" or "notcaught" ? oldStatus : gameStatus;
            var nickname = string.IsNullOrWhiteSpace(pokemon.Nickname) ? null : pokemon.Nickname;

            // Restore remote failure states locally after enriching all other fields from the game.
            if (newStatus is "brofailed" or "notcaught")
            {
                await _knownPokemon.UpsertSoullockeEncounterAsync(
                    mergedId, pokemon.Species, nickname, mappedLocation, newStatus, cancellationToken);
            }

            if (encounter.Pokemon != pokemon.Species || encounter.Nickname != nickname || oldStatus != newStatus)
            {
                encounter.Pokemon = pokemon.Species;
                encounter.Nickname = nickname;
                encounter.Status = newStatus;
                runChanged = true;
                if (newStatus == "fainted" && oldStatus != "fainted") faintedLocations.Add(mappedLocation);
            }
            await _knownPokemon.MarkSoullockeSyncedAsync(mergedId, cancellationToken);
        }

        if (run is not null && runChanged)
        {
            await _soullockeClient.SaveRunAsync(run.Encounters, cancellationToken);
            Console.WriteLine("Soullocke: Begegnungsdaten und Status aktualisiert.");
            foreach (var location in faintedLocations)
                await _soullockeClient.MarkLinkedPartnerBroFailedAsync(location, cancellationToken);
        }
    }

    private static string NormalizeStatus(string? status) => (status ?? "alive").Trim().ToLowerInvariant() switch
    {
        "fainted" => "fainted", "notcaught" => "notcaught", "brofailed" => "brofailed",
        "boxed" => "boxed", _ => "alive"
    };

    private static string CreateUniqueId(PartyPokemon pokemon) =>
        $"{pokemon.Pid}:{pokemon.OriginalTrainerId}:{pokemon.OriginalTrainerSecretId}";
}
