using System.Collections.Concurrent;
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
    private readonly NuzlockeRuleEventSource _ruleEvents;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly Dictionary<string, string> _partnerStatusByLocation =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<string, SoulLinkPartnerInfo> _partnerLinksByLocation =
        new(StringComparer.OrdinalIgnoreCase);

    private bool _initialized;
    private bool _firstLiveSnapshotPersisted;
    private bool _partnerSnapshotInitialized;

    public SyncService(
        IPartySource partySource,
        KnownPokemonStore knownPokemon,
        SoullockeClient soullockeClient,
        LocationMapper locationMapper,
        NuzlockeRuleEventSource ruleEvents,
        AppConfig config)
    {
        _partySource = partySource;
        _knownPokemon = knownPokemon;
        _soullockeClient = soullockeClient;
        _locationMapper = locationMapper;
        _ruleEvents = ruleEvents;
        _config = config;
    }

    public string? PartnerPlayerName => _soullockeClient.PartnerPlayerName;

    public bool TryGetPartnerLink(string location, out SoulLinkPartnerInfo? link)
    {
        if (_partnerLinksByLocation.TryGetValue(NormalizeLocation(location), out var found))
        {
            link = found;
            return true;
        }

        link = null;
        return false;
    }

    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;

            await _knownPokemon.LoadAsync(cancellationToken);

            if (_config.SoullockeEnabled)
            {
                var run = await _soullockeClient.LoadRunAsync(cancellationToken);
                await PullRemoteEncountersAsync(run, cancellationToken);

                var partnerRun = await _soullockeClient.LoadPartnerRunAsync(cancellationToken);
                CapturePartnerSnapshot(partnerRun);
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
            catch
            {
                // Laufende Synchronisierung bleibt aktiv. Technische Debug-Ausgaben sind bewusst deaktiviert.
            }

            await Task.Delay(_config.PollIntervalMilliseconds, cancellationToken);
        }
    }

    private async Task PullRemoteEncountersAsync(
        SoullockeRun run,
        CancellationToken cancellationToken)
    {
        var importedSpecies = new HashSet<int>();

        foreach (var pair in run.Encounters)
        {
            if (pair.Value.Pokemon <= 0 || !importedSpecies.Add(pair.Value.Pokemon))
                continue;

            var displayLocation = ToDisplayLocation(pair.Key);
            await _knownPokemon.UpsertSoullockeEncounterAsync(
                $"soullocke:{_config.PlayerId}:{displayLocation}",
                pair.Value.Pokemon,
                pair.Value.Nickname,
                displayLocation,
                NormalizeStatus(pair.Value.Status),
                cancellationToken);
        }
    }

    private async Task SynchronizeOnceAsync(CancellationToken cancellationToken)
    {
        using var readTimeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        readTimeout.CancelAfter(TimeSpan.FromSeconds(5));

        var slots = await _partySource.ReadAllPokemonAsync(readTimeout.Token);
        var run = _config.SoullockeEnabled
            ? await _soullockeClient.LoadRunAsync(cancellationToken)
            : null;
        var partnerRun = _config.SoullockeEnabled
            ? await _soullockeClient.LoadPartnerRunAsync(cancellationToken)
            : null;

        if (partnerRun is not null)
            UpdatePartnerLinks(partnerRun);
        else if (_config.SoullockeEnabled)
            _partnerLinksByLocation.Clear();

        if (run is not null)
            await PullRemoteEncountersAsync(run, cancellationToken);

        var runChanged = run is not null && CanonicalizeOwnRunLocations(run);
        if (run is not null && partnerRun is not null)
            runChanged |= await ApplyPartnerKnockoutsAsync(run, partnerRun, cancellationToken);

        var processedSpecies = new HashSet<int>();
        var expectedRemoteEntries = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var newSoullockeEntries = new List<(string Species, string? Nickname, string Location)>();

        foreach (var slot in slots
                     .OrderBy(slot => slot.Box is null ? 0 : 1)
                     .ThenBy(slot => slot.SlotId))
        {
            var pokemon = slot.Pokemon;
            if (pokemon is null || pokemon.IsEgg || pokemon.Species <= 0)
                continue;

            if (!processedSpecies.Add(pokemon.Species))
                continue;

            var displayLocation = _locationMapper.GetLocationName(pokemon.LocationMet)
                                  ?? $"Unbekannter Fangort ({pokemon.LocationMet})";

            var gameEntry = new KnownPokemonEntry
            {
                UniqueId = CreateUniqueId(pokemon),
                SpeciesId = pokemon.Species,
                Species = pokemon.SpeciesName,
                Nickname = pokemon.Nickname,
                Pid = pokemon.Pid,
                OriginalTrainerId = pokemon.OriginalTrainerId,
                OriginalTrainerSecretId = pokemon.OriginalTrainerSecretId,
                Location = displayLocation,
                LocationId = pokemon.LocationMet,
                LevelMet = pokemon.LevelMet,
                CurrentLevel = pokemon.Level,
                CurrentHp = pokemon.Hp.Current,
                MaxHp = pokemon.Hp.Max,
                IsEgg = pokemon.IsEgg
            };

            var existedInSoulBuddy = await _knownPokemon.FindBySpeciesAsync(
                pokemon.Species,
                cancellationToken) is not null;

            var mergedId = await _knownPokemon.MergeGamePokemonAsync(
                gameEntry.UniqueId,
                gameEntry,
                slot.Box is not null,
                cancellationToken);

            if (!existedInSoulBuddy)
            {
                Console.WriteLine(
                    $"Neuer Pokémon-Eintrag in SoulBuddy: {FormatPokemon(pokemon.SpeciesName, pokemon.Nickname)} " +
                    $"– Fangort: {displayLocation}.");
            }

            if (run is null)
                continue;

            var canonicalLocationKey = FindLocationKey(run.Encounters, displayLocation);
            var sameSpeciesPair = run.Encounters.FirstOrDefault(
                pair => pair.Value.Pokemon == pokemon.Species);
            var sameSpeciesKey = sameSpeciesPair.Value is null ? null : sameSpeciesPair.Key;
            var remoteKey = canonicalLocationKey ?? displayLocation;
            var isNewRemoteEntry = canonicalLocationKey is null && sameSpeciesKey is null;

            if (canonicalLocationKey is null && sameSpeciesKey is not null)
            {
                var existingEncounter = run.Encounters[sameSpeciesKey];
                run.Encounters.Remove(sameSpeciesKey);
                run.Encounters[displayLocation] = existingEncounter;
                remoteKey = displayLocation;
                runChanged = true;
            }

            expectedRemoteEntries[remoteKey] = pokemon.Species;

            if (!run.Encounters.TryGetValue(remoteKey, out var encounter))
            {
                encounter = new SoullockeEncounter();
                run.Encounters[remoteKey] = encounter;
                runChanged = true;
            }

            var oldStatus = NormalizeStatus(encounter.Status);
            var gameStatus = pokemon.Hp.Current <= 0
                ? "fainted"
                : slot.Box is not null ? "boxed" : "alive";
            var newStatus = oldStatus is "brofailed" or "notcaught" ? oldStatus : gameStatus;
            var nickname = string.IsNullOrWhiteSpace(pokemon.Nickname) ? null : pokemon.Nickname;

            if (encounter.Pokemon != pokemon.Species ||
                !string.Equals(encounter.Nickname, nickname, StringComparison.Ordinal) ||
                oldStatus != newStatus)
            {
                runChanged = true;
            }

            encounter.Pokemon = pokemon.Species;
            encounter.Nickname = nickname;
            encounter.Status = newStatus;

            if (isNewRemoteEntry)
                newSoullockeEntries.Add((pokemon.SpeciesName, nickname, displayLocation));

            if (newStatus is "brofailed" or "notcaught")
            {
                await _knownPokemon.UpsertSoullockeEncounterAsync(
                    mergedId,
                    pokemon.Species,
                    nickname,
                    displayLocation,
                    newStatus,
                    cancellationToken);
            }

            await _knownPokemon.MarkSoullockeSyncedAsync(mergedId, cancellationToken);
        }

        // A partner may already be fainted before the linked local Pokémon is first
        // observed in this cycle. Re-apply the SoulLink rule after local encounters
        // were merged so that the new entry immediately becomes brofailed.
        if (run is not null && partnerRun is not null)
            runChanged |= await ApplyPartnerKnockoutsAsync(run, partnerRun, cancellationToken, publishTransitions: false);

        var hasFreshCollectorData =
            _partySource is LivePartySource livePartySource &&
            livePartySource.HasReceivedLiveUpdate &&
            processedSpecies.Count > 0;

        var forceInitialLiveSave =
            run is not null &&
            hasFreshCollectorData &&
            !_firstLiveSnapshotPersisted;

        if (run is null || (!runChanged && !forceInitialLiveSave))
            return;

        await _soullockeClient.SaveRunAsync(run.Encounters, cancellationToken);
        var verifiedRun = await _soullockeClient.LoadRunAsync(cancellationToken);

        var missingAfterSave = expectedRemoteEntries.Any(expected =>
            !verifiedRun.Encounters.TryGetValue(expected.Key, out var saved) ||
            saved.Pokemon != expected.Value);

        if (missingAfterSave)
            throw new InvalidOperationException("Soullocke hat nicht alle lokalen Begegnungen bestätigt.");

        _firstLiveSnapshotPersisted = true;

        foreach (var entry in newSoullockeEntries)
        {
            Console.WriteLine(
                $"Neuer Pokémon-Eintrag in SoulLocke: {FormatPokemon(entry.Species, entry.Nickname)} " +
                $"– Fangort: {entry.Location}.");
        }
    }

    private void CapturePartnerSnapshot(SoullockeRun? partnerRun)
    {
        _partnerStatusByLocation.Clear();
        _partnerLinksByLocation.Clear();

        if (partnerRun is not null)
        {
            UpdatePartnerLinks(partnerRun);
            foreach (var pair in partnerRun.Encounters)
                _partnerStatusByLocation[NormalizeLocation(pair.Key)] = NormalizeStatus(pair.Value.Status);
        }

        _partnerSnapshotInitialized = true;
    }

    private void UpdatePartnerLinks(SoullockeRun partnerRun)
    {
        var currentLocations = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in partnerRun.Encounters)
        {
            if (pair.Value.Pokemon <= 0)
                continue;

            var location = ToDisplayLocation(pair.Key);
            var normalizedLocation = NormalizeLocation(location);
            currentLocations.Add(normalizedLocation);
            _partnerLinksByLocation[normalizedLocation] = new SoulLinkPartnerInfo
            {
                PlayerName = _soullockeClient.PartnerPlayerName ?? "Partner",
                SpeciesId = pair.Value.Pokemon,
                Nickname = pair.Value.Nickname,
                Location = location,
                Status = NormalizeStatus(pair.Value.Status)
            };
        }

        foreach (var key in _partnerLinksByLocation.Keys)
        {
            if (!currentLocations.Contains(key))
                _partnerLinksByLocation.TryRemove(key, out _);
        }
    }

    private async Task<bool> ApplyPartnerKnockoutsAsync(
        SoullockeRun ownRun,
        SoullockeRun partnerRun,
        CancellationToken cancellationToken,
        bool publishTransitions = true)
    {
        var changed = false;

        foreach (var pair in partnerRun.Encounters)
        {
            var normalizedLocation = NormalizeLocation(pair.Key);
            var currentStatus = NormalizeStatus(pair.Value.Status);
            _partnerStatusByLocation.TryGetValue(normalizedLocation, out var previousStatus);

            if (currentStatus == "fainted")
            {
                var ownKey = FindLocationKey(ownRun.Encounters, pair.Key);
                SoullockeEncounter? ownEncounter = null;
                KnownPokemonEntry? linkedLocal = null;

                if (ownKey is not null && ownRun.Encounters.TryGetValue(ownKey, out ownEncounter) &&
                    ownEncounter.Pokemon > 0)
                {
                    var ownStatus = NormalizeStatus(ownEncounter.Status);
                    if (ownStatus is not "fainted" and not "notcaught" and not "brofailed")
                    {
                        ownEncounter.Status = "brofailed";
                        changed = true;
                    }

                    linkedLocal = await _knownPokemon.FindBySpeciesAsync(
                        ownEncounter.Pokemon,
                        cancellationToken);

                    await _knownPokemon.UpsertSoullockeEncounterAsync(
                        linkedLocal?.UniqueId ?? $"soullocke:{_config.PlayerId}:{ownKey}",
                        ownEncounter.Pokemon,
                        ownEncounter.Nickname,
                        ownKey,
                        "brofailed",
                        cancellationToken);
                }

                var isTransition = _partnerSnapshotInitialized && previousStatus != "fainted";
                if (publishTransitions && isTransition)
                {
                    var partnerSpeciesName = $"Pokémon #{pair.Value.Pokemon}";
                    _ruleEvents.PublishPartnerPokemonKnockedOut(
                        _soullockeClient.PartnerPlayerName ?? "Partner",
                        pair.Value.Pokemon,
                        partnerSpeciesName,
                        pair.Value.Nickname,
                        ToDisplayLocation(pair.Key),
                        ownEncounter?.Pokemon,
                        linkedLocal?.Species,
                        ownEncounter?.Nickname ?? linkedLocal?.Nickname);
                }
            }

            _partnerStatusByLocation[normalizedLocation] = currentStatus;
        }

        _partnerSnapshotInitialized = true;
        return changed;
    }

    private static bool CanonicalizeOwnRunLocations(SoullockeRun run)
    {
        var changed = false;

        foreach (var pair in run.Encounters.ToArray())
        {
            var canonicalKey = ToDisplayLocation(pair.Key);
            if (string.Equals(pair.Key, canonicalKey, StringComparison.Ordinal))
                continue;

            if (run.Encounters.TryGetValue(canonicalKey, out var existing))
            {
                if (existing.Pokemon <= 0 && pair.Value.Pokemon > 0)
                {
                    existing.Pokemon = pair.Value.Pokemon;
                    existing.Nickname = pair.Value.Nickname;
                    existing.Status = pair.Value.Status;
                }

                run.Encounters.Remove(pair.Key);
                changed = true;
                continue;
            }

            run.Encounters.Remove(pair.Key);
            run.Encounters[canonicalKey] = pair.Value;
            changed = true;
        }

        return changed;
    }

    private static string ToDisplayLocation(string remoteLocation) =>
        remoteLocation.Trim() switch
        {
            "Finsterhöhle" or "Dark Cave" or "Placeholder 1" => "Dunkelhöhle",
            "Sprout Tower" or "Placeholder 2" or "" => "Knofensaturm",
            _ => remoteLocation.Trim()
        };

    private static string? FindLocationKey(
        IReadOnlyDictionary<string, SoullockeEncounter> encounters,
        string location)
    {
        var normalized = NormalizeLocation(location);
        return encounters.Keys.FirstOrDefault(
            key => NormalizeLocation(key) == normalized);
    }

    private static string NormalizeLocation(string value) =>
        new(value.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());

    private static string NormalizeStatus(string? status) =>
        (status ?? "alive").Trim().ToLowerInvariant() switch
        {
            "fainted" => "fainted",
            "notcaught" => "notcaught",
            "brofailed" => "brofailed",
            "boxed" => "boxed",
            _ => "alive"
        };

    private static string CreateUniqueId(PartyPokemon pokemon) =>
        $"{pokemon.Pid}:{pokemon.OriginalTrainerId}:{pokemon.OriginalTrainerSecretId}";

    private static string FormatPokemon(string species, string? nickname) =>
        string.IsNullOrWhiteSpace(nickname)
            ? species
            : $"{nickname} ({species})";
}
