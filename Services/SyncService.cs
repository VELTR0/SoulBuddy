using System.Runtime.CompilerServices;
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
    private bool _firstLiveSnapshotPersisted;
    private long _syncCycle;

    public SyncService(IPartySource partySource, KnownPokemonStore knownPokemon,
        SoullockeClient soullockeClient, LocationMapper locationMapper, AppConfig config)
    {
        _partySource = partySource;
        _knownPokemon = knownPokemon;
        _soullockeClient = soullockeClient;
        _locationMapper = locationMapper;
        _config = config;

        Console.WriteLine(
            $"[LIVE-PARTY-INSTANCE] SyncService verwendet {partySource.GetType().Name} " +
            $"#{RuntimeHelpers.GetHashCode(partySource)}.");
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
        finally
        {
            _initializationLock.Release();
        }
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        await InitializeAsync(cancellationToken);
        Console.WriteLine($"SoulSync läuft. Polling-Intervall: {_config.PollIntervalMilliseconds} ms.");
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await SynchronizeOnceAsync(cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [SOULLOCKE-SYNC] Fehler: {ex}");
            }

            await Task.Delay(_config.PollIntervalMilliseconds, cancellationToken);
        }
    }

    private async Task ImportSoullockeRunAsync(CancellationToken cancellationToken)
    {
        var run = await _soullockeClient.LoadRunAsync(cancellationToken);
        Console.WriteLine($"[SOULLOCKE-IMPORT] Remote-Run enthält {run.Encounters.Count} Begegnungen.");
        foreach (var pair in run.Encounters)
        {
            Console.WriteLine(
                $"[SOULLOCKE-IMPORT] Ort='{pair.Key}', Pokémon={pair.Value.Pokemon}, " +
                $"Spitzname='{pair.Value.Nickname ?? "<leer>"}', Status='{pair.Value.Status}'.");
        }
        await PullRemoteEncountersAsync(run, cancellationToken);
        Console.WriteLine($"Soullocke-Initialisierung: {run.Encounters.Count} Begegnungen mit Status gelesen.");
    }

    private async Task PullRemoteEncountersAsync(SoullockeRun run, CancellationToken cancellationToken)
    {
        var importedSpecies = new HashSet<int>();
        foreach (var pair in run.Encounters)
        {
            if (pair.Value.Pokemon <= 0)
            {
                Console.WriteLine($"[SOULLOCKE-PULL] Übersprungen: Ort='{pair.Key}' hat keine gültige Pokémon-ID ({pair.Value.Pokemon}).");
                continue;
            }

            if (!importedSpecies.Add(pair.Value.Pokemon))
            {
                Console.WriteLine($"[SOULLOCKE-PULL] Übersprungen: Pokémon #{pair.Value.Pokemon} wurde im Remote-Run bereits verarbeitet (Ort='{pair.Key}').");
                continue;
            }

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
        if (!_initialized)
            throw new InvalidOperationException("Der Soullocke-Startimport wurde noch nicht abgeschlossen.");

        var cycle = Interlocked.Increment(ref _syncCycle);
        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] [SOULLOCKE-SYNC #{cycle}] Durchlauf gestartet; " +
            $"lokale Slots werden aus {_partySource.GetType().Name} " +
            $"#{RuntimeHelpers.GetHashCode(_partySource)} gelesen …");

        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(5));

        IReadOnlyList<PartySlot> slots;
        try
        {
            slots = await _partySource.ReadAllPokemonAsync(readTimeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                "LivePartySource.ReadAllPokemonAsync hat nach 5 Sekunden nicht geantwortet. " +
                "Wahrscheinlich blockiert ein Party-/Box-Update den internen Lock.");
        }

        Console.WriteLine($"[{DateTime.Now:HH:mm:ss}] [SOULLOCKE-SYNC #{cycle}] Lokale Slots gelesen: {slots.Count}.");

        SoullockeRun? run = _config.SoullockeEnabled
            ? await _soullockeClient.LoadRunAsync(cancellationToken)
            : null;

        if (run is not null)
        {
            Console.WriteLine($"[SOULLOCKE-SYNC #{cycle}] Remote vor Merge: {run.Encounters.Count} Begegnungen: " +
                              string.Join(", ", run.Encounters.Select(pair => $"'{pair.Key}'=#{pair.Value.Pokemon}")));
            await PullRemoteEncountersAsync(run, cancellationToken);
        }

        var runChanged = false;
        var faintedLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var processedSpecies = new HashSet<int>();
        var expectedRemoteEntries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var validPokemonCount = 0;

        foreach (var slot in slots.OrderBy(slot => slot.Box is null ? 0 : 1).ThenBy(slot => slot.SlotId))
        {
            var pokemon = slot.Pokemon;
            var source = slot.Box is null ? $"Team-Slot {slot.SlotId}" : $"Box {slot.Box}, Slot {slot.SlotId}";

            if (pokemon is null)
            {
                Console.WriteLine($"[SOULLOCKE-SYNC #{cycle}] {source}: leer.");
                continue;
            }

            Console.WriteLine(
                $"[SOULLOCKE-SYNC #{cycle}] {source}: '{pokemon.Nickname ?? "<kein Spitzname>"}' / " +
                $"{pokemon.SpeciesName} (#{pokemon.Species}), PID={pokemon.Pid}, Ei={pokemon.IsEgg}, " +
                $"Fangort-ID={pokemon.LocationMet}, Level={pokemon.Level}, KP={pokemon.Hp.Current}/{pokemon.Hp.Max}.");

            if (pokemon.IsEgg || pokemon.Species <= 0)
            {
                Console.WriteLine($"[SOULLOCKE-SYNC #{cycle}] {source}: übersprungen wegen Ei oder ungültiger Spezies-ID.");
                continue;
            }

            validPokemonCount++;
            if (!processedSpecies.Add(pokemon.Species))
            {
                Console.WriteLine($"[SOULLOCKE-SYNC #{cycle}] {source}: übersprungen, Spezies #{pokemon.Species} wurde bereits in diesem Durchlauf verarbeitet.");
                continue;
            }

            var mappedLocation = _locationMapper.GetLocationName(pokemon.LocationMet);
            var location = mappedLocation ?? $"Unbekannter Fangort ({pokemon.LocationMet})";
            Console.WriteLine(
                $"[SOULLOCKE-SYNC #{cycle}] {pokemon.SpeciesName}: Fangort-ID {pokemon.LocationMet} => " +
                $"Mapper='{mappedLocation ?? "<kein Treffer>"}', verwendet='{location}', normalisiert='{NormalizeLocation(location)}'.");

            var gameEntry = new KnownPokemonEntry
            {
                UniqueId = CreateUniqueId(pokemon),
                SpeciesId = pokemon.Species,
                Species = pokemon.SpeciesName,
                Nickname = pokemon.Nickname,
                Pid = pokemon.Pid,
                OriginalTrainerId = pokemon.OriginalTrainerId,
                OriginalTrainerSecretId = pokemon.OriginalTrainerSecretId,
                Location = location,
                LocationId = pokemon.LocationMet,
                LevelMet = pokemon.LevelMet,
                CurrentLevel = pokemon.Level,
                CurrentHp = pokemon.Hp.Current,
                MaxHp = pokemon.Hp.Max,
                IsEgg = pokemon.IsEgg
            };

            var mergedId = await _knownPokemon.MergeGamePokemonAsync(
                gameEntry.UniqueId, gameEntry, slot.Box is not null, cancellationToken);
            Console.WriteLine($"[SOULLOCKE-SYNC #{cycle}] {pokemon.SpeciesName}: lokal zusammengeführt unter ID '{mergedId}'.");

            if (run is null)
                continue;

            var matchingLocationKey = FindLocationKey(run.Encounters, location);
            var sameSpeciesPair = run.Encounters.FirstOrDefault(pair => pair.Value.Pokemon == pokemon.Species);
            var sameSpeciesKey = sameSpeciesPair.Value is null ? null : sameSpeciesPair.Key;
            var remoteKey = matchingLocationKey ?? sameSpeciesKey ?? location;
            expectedRemoteEntries[remoteKey] = pokemon.Species;

            Console.WriteLine(
                $"[SOULLOCKE-SYNC #{cycle}] {pokemon.SpeciesName}: Remote-Match Ort='{matchingLocationKey ?? "<keins>"}', " +
                $"Spezies='{sameSpeciesKey ?? "<keins>"}', Ziel-Key='{remoteKey}'.");

            if (!run.Encounters.TryGetValue(remoteKey, out var encounter))
            {
                encounter = new SoullockeEncounter();
                run.Encounters[remoteKey] = encounter;
                Console.WriteLine($"[SOULLOCKE-SYNC #{cycle}] {pokemon.SpeciesName}: neuer Remote-Eintrag unter '{remoteKey}' angelegt.");
            }

            var oldStatus = NormalizeStatus(encounter.Status);
            var gameStatus = pokemon.Hp.Current <= 0
                ? "fainted"
                : slot.Box is not null ? "boxed" : "alive";
            var newStatus = oldStatus is "brofailed" or "notcaught" ? oldStatus : gameStatus;
            var nickname = string.IsNullOrWhiteSpace(pokemon.Nickname) ? null : pokemon.Nickname;

            var speciesChanged = encounter.Pokemon != pokemon.Species;
            var nicknameChanged = !string.Equals(encounter.Nickname, nickname, StringComparison.Ordinal);
            var statusChanged = oldStatus != newStatus;

            encounter.Pokemon = pokemon.Species;
            encounter.Nickname = nickname;
            encounter.Status = newStatus;

            if (speciesChanged || nicknameChanged || statusChanged)
            {
                runChanged = true;
                Console.WriteLine(
                    $"[SOULLOCKE-SYNC #{cycle}] VORGEMERKT {pokemon.SpeciesName}: " +
                    $"speciesChanged={speciesChanged}, nicknameChanged={nicknameChanged}, statusChanged={statusChanged}, Ort='{remoteKey}'.");
                if (newStatus == "fainted" && oldStatus != "fainted")
                    faintedLocations.Add(remoteKey);
            }
            else
            {
                Console.WriteLine($"[SOULLOCKE-SYNC #{cycle}] {pokemon.SpeciesName}: Remote-Daten identisch; Eintrag bleibt Bestandteil des vollständigen Snapshots.");
            }

            if (newStatus is "brofailed" or "notcaught")
            {
                await _knownPokemon.UpsertSoullockeEncounterAsync(
                    mergedId, pokemon.Species, nickname, remoteKey, newStatus, cancellationToken);
            }

            await _knownPokemon.MarkSoullockeSyncedAsync(mergedId, cancellationToken);
        }

        var hasFreshCollectorData =
            _partySource is LivePartySource livePartySource &&
            livePartySource.HasReceivedLiveUpdate &&
            processedSpecies.Count > 0;

        var forceInitialLiveSave =
            run is not null &&
            hasFreshCollectorData &&
            !_firstLiveSnapshotPersisted;

        Console.WriteLine(
            $"[SOULLOCKE-SYNC #{cycle}] Auswertung: gültige lokale Pokémon={validPokemonCount}, " +
            $"eindeutige Spezies={processedSpecies.Count}, runChanged={runChanged}, " +
            $"forceInitialLiveSave={forceInitialLiveSave}, Remote-Einträge danach={run?.Encounters.Count ?? 0}.");

        if (run is not null && (runChanged || forceInitialLiveSave))
        {
            Console.WriteLine(
                forceInitialLiveSave && !runChanged
                    ? $"[SOULLOCKE-SYNC #{cycle}] Erzwinge vollständigen ersten Live-Snapshot, obwohl LOAD identische Daten gemeldet hat."
                    : $"[SOULLOCKE-SYNC #{cycle}] Änderungen werden gespeichert.");

            Console.WriteLine($"[SOULLOCKE-SYNC #{cycle}] SAVE START: " +
                              string.Join(", ", run.Encounters.Select(pair => $"'{pair.Key}'=#{pair.Value.Pokemon}/{pair.Value.Status}")));

            await _soullockeClient.SaveRunAsync(run.Encounters, cancellationToken);
            var verifiedRun = await _soullockeClient.LoadRunAsync(cancellationToken);

            var missingAfterSave = expectedRemoteEntries
                .Where(expected =>
                    !verifiedRun.Encounters.TryGetValue(expected.Key, out var saved) ||
                    saved.Pokemon != expected.Value)
                .Select(expected => $"'{expected.Key}'=#{expected.Value}")
                .ToArray();

            if (missingAfterSave.Length > 0)
            {
                throw new InvalidOperationException(
                    "Soullocke hat nach dem Save folgende lokale Begegnungen nicht bestätigt: " +
                    string.Join(", ", missingAfterSave));
            }

            _firstLiveSnapshotPersisted = true;
            Console.WriteLine(
                $"[SOULLOCKE-SYNC #{cycle}] SAVE VERIFIZIERT: " +
                $"{expectedRemoteEntries.Count} lokale Begegnungen wurden von Soullocke bestätigt.");

            foreach (var location in faintedLocations)
                await _soullockeClient.MarkLinkedPartnerBroFailedAsync(location, cancellationToken);
        }
        else if (run is not null)
        {
            Console.WriteLine($"[SOULLOCKE-SYNC #{cycle}] Kein Save: noch keine frischen Collector-Daten oder Snapshot bereits persistiert.");
        }
    }

    private static string? FindLocationKey(
        IReadOnlyDictionary<string, SoullockeEncounter> encounters,
        string location)
    {
        var normalized = NormalizeLocation(location);
        return encounters.Keys.FirstOrDefault(key => NormalizeLocation(key) == normalized);
    }

    private static string NormalizeLocation(string value) =>
        new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string NormalizeStatus(string? status) => (status ?? "alive").Trim().ToLowerInvariant() switch
    {
        "fainted" => "fainted",
        "notcaught" => "notcaught",
        "brofailed" => "brofailed",
        "boxed" => "boxed",
        _ => "alive"
    };

    private static string CreateUniqueId(PartyPokemon pokemon) =>
        $"{pokemon.Pid}:{pokemon.OriginalTrainerId}:{pokemon.OriginalTrainerSecretId}";
}
