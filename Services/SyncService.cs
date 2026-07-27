using SoulSync.Data;
using SoulSync.Models;

namespace SoulSync.Services;

public sealed class SyncService
{
    private readonly AppConfig _config;
    private readonly PartyReader _partyReader;
    private readonly SoullockeClient _soullockeClient;
    private readonly KnownPokemonStore _knownPokemon;
    private readonly LocationMapper _locationMapper;

    public SyncService(
        AppConfig config,
        PartyReader partyReader,
        SoullockeClient soullockeClient,
        KnownPokemonStore knownPokemon,
        LocationMapper locationMapper)
    {
        _config = config;
        _partyReader = partyReader;
        _soullockeClient = soullockeClient;
        _knownPokemon = knownPokemon;
        _locationMapper = locationMapper;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await _knownPokemon.LoadAsync(cancellationToken);

        Console.WriteLine("SoulSync läuft.");
        Console.WriteLine($"party.json: {_config.PartyJsonPath}");
        Console.WriteLine($"Spieler: {_config.PlayerId}");
        Console.WriteLine($"DryRun: {_config.DryRun}");
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

    private async Task SynchronizeOnceAsync(
        CancellationToken cancellationToken)
    {
        var party = await _partyReader.ReadAsync(
            _config.PartyJsonPath,
            cancellationToken);

        foreach (var slot in party)
        {
            var pokemon = slot.Pokemon;

            if (pokemon is null || pokemon.IsEgg || pokemon.Species <= 0)
            {
                continue;
            }

            var uniqueId = CreateUniqueId(pokemon);

            if (_knownPokemon.Contains(uniqueId))
            {
                continue;
            }

            var locationName =
                _locationMapper.GetLocationName(pokemon.LocationMet);

            Console.WriteLine(
                $"[{DateTime.Now:HH:mm:ss}] Neues Pokémon erkannt: " +
                $"{pokemon.Nickname} ({pokemon.SpeciesName}), " +
                $"PID {pokemon.Pid}, Fangort-ID {pokemon.LocationMet}");

            if (locationName is null)
            {
                Console.WriteLine(
                    $"  Fangort-ID {pokemon.LocationMet} ist noch nicht zugeordnet.");

                // Noch nicht als bekannt speichern, damit es erneut versucht wird.
                continue;
            }

            var run = await _soullockeClient.LoadRunAsync(
                cancellationToken);

            if (run.Encounters.ContainsKey(locationName))
            {
                Console.WriteLine(
                    $"  Nicht importiert: {locationName} ist bereits belegt.");

                // Damit dieselbe Meldung nicht jede Sekunde erscheint.
                await _knownPokemon.AddAsync(
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
                    $"„{locationName}“ mit Status „{encounter.Status}“ eintragen.");
            }
            else
            {
                run.Encounters[locationName] = encounter;

                await _soullockeClient.SaveRunAsync(
                    run.Encounters,
                    cancellationToken);

                Console.WriteLine(
                    $"  Erfolgreich unter „{locationName}“ eingetragen.");
            }

            await _knownPokemon.AddAsync(
                uniqueId,
                cancellationToken);
        }
    }

    private static string CreateUniqueId(PartyPokemon pokemon)
    {
        return $"{pokemon.Pid}:{pokemon.OriginalTrainerId}:" +
               $"{pokemon.OriginalTrainerSecretId}";
    }
}