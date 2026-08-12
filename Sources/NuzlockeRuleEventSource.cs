using SoulBuddy.Models;
using SoulBuddy.Services;

namespace SoulBuddy.Sources;

public sealed class NuzlockeRuleEventSource
{
    private static readonly TimeSpan CatchResolutionGracePeriod = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan BoxResolutionGracePeriod = TimeSpan.FromSeconds(10);

    private readonly LocationMapper _locationMapper;
    private readonly Dictionary<long, int> _lastHpByPid = [];
    private readonly HashSet<string> _encounteredLocations = new(StringComparer.OrdinalIgnoreCase);
    private readonly object _catchSync = new();
    private readonly object _partyBoxSync = new();
    private readonly Dictionary<int, PartyPokemon> _partyBySlot = [];
    private readonly Dictionary<(int Box, int Slot), long> _boxIdentityBySlot = [];
    private readonly Dictionary<long, PendingBoxTransfer> _pendingBoxTransfers = [];
    private ActiveCatchEncounter? _activeEncounter;
    private CancellationTokenSource? _pendingCatchFailure;
    private bool _wasInBattle;

    public NuzlockeRuleEventSource(LocationMapper locationMapper)
    {
        _locationMapper = locationMapper;
    }

    public event EventHandler<NuzlockeRuleEvent>? EventOccurred;

    public void ObservePlayerState(PlayerLiveState state)
    {
        ActiveCatchEncounter? encounterToResolve = null;
        NuzlockeRuleEvent? previousEncounterFailure = null;
        NuzlockeRuleEvent? catchableEncounterEvent = null;

        lock (_catchSync)
        {
            if (_wasInBattle && !state.InBattle && _activeEncounter is not null)
            {
                if (_activeEncounter.IsCatchable && !_activeEncounter.WasCaught)
                {
                    // HGSS can report the field state before the collector's next
                    // party/box refresh contains the freshly caught Pokemon. Keep the
                    // encounter alive briefly so that update can win over CatchFailed.
                    _activeEncounter.BattleEnded = true;
                    encounterToResolve = _activeEncounter;
                }
                else
                {
                    CancelPendingCatchFailureLocked();
                    _activeEncounter = null;
                }
            }

            if (state.InBattle &&
                string.Equals(state.BattleKind, "wild", StringComparison.OrdinalIgnoreCase) &&
                state.Opponent is not null &&
                state.Opponent.SpeciesId > 0)
            {
                var opponent = state.Opponent;
                var isNewBattle = _activeEncounter is null ||
                                  (_activeEncounter.Pokemon.Pid != 0 &&
                                   opponent.Pid != 0 &&
                                   _activeEncounter.Pokemon.Pid != opponent.Pid);

                if (isNewBattle)
                {
                    // Starting another battle is definitive evidence that an older,
                    // already-ended pending encounter was not caught.
                    if (_activeEncounter is
                        {
                            BattleEnded: true,
                            IsCatchable: true,
                            WasCaught: false
                        } pendingEncounter)
                    {
                        previousEncounterFailure = CreateCatchFailedEvent(pendingEncounter);
                        CancelPendingCatchFailureLocked();
                        _activeEncounter = null;
                    }

                    // The opponent's LocationMet is Pokémon metadata, not the current
                    // HGSS field location. Feeding it into LocationMapper can turn a
                    // wild Johto encounter into a Sinnoh route (for example Route 221).
                    // The collector already provides the canonical current HGSS area.
                    var locationId = state.LocationId;
                    var locationName = ResolveLiveLocationName(state.LocationName, locationId);
                    var locationKey = !IsUnknownLiveLocation(locationName)
                        ? $"name:{NormalizeEncounterLocation(locationName)}"
                        : locationId is > 0
                            ? $"live-id:{locationId.Value}"
                            : "unknown";
                    var isFirstEncounter = _encounteredLocations.Add(locationKey);
                    var isCatchable = isFirstEncounter || opponent.IsShiny;

                    _activeEncounter = new ActiveCatchEncounter(
                        opponent,
                        locationName,
                        locationId,
                        isFirstEncounter,
                        isCatchable);

                    if (isCatchable)
                    {
                        catchableEncounterEvent = new NuzlockeRuleEvent
                        {
                            Type = NuzlockeRuleEventType.CatchableEncounter,
                            OccurredAt = DateTimeOffset.Now,
                            SpeciesId = opponent.SpeciesId,
                            SpeciesName = opponent.SpeciesName,
                            Nickname = opponent.Nickname,
                            LocationName = locationName,
                            LocationId = locationId,
                            Pid = opponent.Pid,
                            IsShiny = opponent.IsShiny,
                            IsFirstEncounter = isFirstEncounter
                        };
                    }
                }
            }

            _wasInBattle = state.InBattle;
        }

        if (previousEncounterFailure is not null)
            Publish(previousEncounterFailure);

        if (encounterToResolve is not null)
            ScheduleCatchFailure(encounterToResolve);

        if (catchableEncounterEvent is not null)
            Publish(catchableEncounterEvent);
    }

    public void ObservePartyUpdate(IEnumerable<PartySlot> slots)
    {
        var update = slots.ToArray();
        ObservePokemonUpdate(update);

        foreach (var boxedEvent in TrackPartyUpdate(update))
            Publish(boxedEvent);
    }

    public void ObserveBoxUpdate(IEnumerable<PartySlot> slots)
    {
        var update = slots.ToArray();
        ObservePokemonUpdate(update);

        foreach (var boxedEvent in TrackBoxUpdate(update))
            Publish(boxedEvent);
    }

    private void ObservePokemonUpdate(IEnumerable<PartySlot> slots)
    {
        foreach (var slot in slots)
        {
            var pokemon = slot.Pokemon;
            if (pokemon is null || pokemon.Species <= 0 || pokemon.IsEgg)
                continue;

            var identity = GetPokemonIdentity(pokemon);

            if (_lastHpByPid.TryGetValue(identity, out var previousHp) &&
                previousHp > 0 &&
                pokemon.Hp.Current <= 0)
            {
                Publish(new NuzlockeRuleEvent
                {
                    Type = NuzlockeRuleEventType.PokemonKnockedOut,
                    OccurredAt = DateTimeOffset.Now,
                    SpeciesId = pokemon.Species,
                    SpeciesName = pokemon.SpeciesName,
                    Nickname = pokemon.Nickname,
                    LocationName = _locationMapper.GetLocationName(pokemon.LocationMet)
                                   ?? $"Unbekannter Fangort ({pokemon.LocationMet})",
                    LocationId = pokemon.LocationMet > 0 ? pokemon.LocationMet : null,
                    Pid = pokemon.Pid,
                    IsShiny = pokemon.IsShiny
                });
            }

            _lastHpByPid[identity] = pokemon.Hp.Current;

            var catchSucceededEvent = TryResolveCaughtPokemon(pokemon);
            if (catchSucceededEvent is not null)
                Publish(catchSucceededEvent);
        }
    }

    private IReadOnlyList<NuzlockeRuleEvent> TrackPartyUpdate(
        IReadOnlyList<PartySlot> slots)
    {
        var boxedEvents = new List<NuzlockeRuleEvent>();
        var now = DateTimeOffset.UtcNow;

        lock (_partyBoxSync)
        {
            RemoveExpiredBoxTransfersLocked(now);

            var previousByIdentity = _partyBySlot.Values
                .Where(IsUsablePokemon)
                .GroupBy(GetPokemonIdentity)
                .ToDictionary(group => group.Key, group => group.First());

            foreach (var slot in slots)
            {
                if (slot.Box is not null)
                    continue;

                if (IsUsablePokemon(slot.Pokemon))
                    _partyBySlot[slot.SlotId] = slot.Pokemon!;
                else
                    _partyBySlot.Remove(slot.SlotId);
            }

            var currentIdentities = _partyBySlot.Values
                .Where(IsUsablePokemon)
                .Select(GetPokemonIdentity)
                .ToHashSet();

            // A Pokémon that still exists somewhere in the six party slots was only
            // reordered. Never treat a slot change alone as a box transfer.
            foreach (var identity in currentIdentities)
                _pendingBoxTransfers.Remove(identity);

            foreach (var pair in previousByIdentity)
            {
                if (currentIdentities.Contains(pair.Key))
                    continue;

                _pendingBoxTransfers[pair.Key] = new PendingBoxTransfer(pair.Value, now);
            }

            ResolveConfirmedBoxTransfersLocked(boxedEvents);
        }

        return boxedEvents;
    }

    private IReadOnlyList<NuzlockeRuleEvent> TrackBoxUpdate(
        IReadOnlyList<PartySlot> slots)
    {
        var boxedEvents = new List<NuzlockeRuleEvent>();
        var now = DateTimeOffset.UtcNow;

        lock (_partyBoxSync)
        {
            RemoveExpiredBoxTransfersLocked(now);

            foreach (var slot in slots)
            {
                if (slot.Box is not int box)
                    continue;

                var key = (box, slot.SlotId);
                if (IsUsablePokemon(slot.Pokemon))
                    _boxIdentityBySlot[key] = GetPokemonIdentity(slot.Pokemon!);
                else
                    _boxIdentityBySlot.Remove(key);
            }

            ResolveConfirmedBoxTransfersLocked(boxedEvents);
        }

        return boxedEvents;
    }

    private void ResolveConfirmedBoxTransfersLocked(
        ICollection<NuzlockeRuleEvent> boxedEvents)
    {
        if (_pendingBoxTransfers.Count == 0 || _boxIdentityBySlot.Count == 0)
            return;

        var boxedIdentities = _boxIdentityBySlot.Values.ToHashSet();
        var resolvedIdentities = _pendingBoxTransfers.Keys
            .Where(boxedIdentities.Contains)
            .ToArray();

        foreach (var identity in resolvedIdentities)
        {
            var transfer = _pendingBoxTransfers[identity];
            _pendingBoxTransfers.Remove(identity);
            boxedEvents.Add(CreatePokemonBoxedEvent(transfer.Pokemon));
        }
    }

    private void RemoveExpiredBoxTransfersLocked(DateTimeOffset now)
    {
        var expired = _pendingBoxTransfers
            .Where(pair => now - pair.Value.RemovedAt > BoxResolutionGracePeriod)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var identity in expired)
            _pendingBoxTransfers.Remove(identity);
    }

    private NuzlockeRuleEvent CreatePokemonBoxedEvent(PartyPokemon pokemon) =>
        new()
        {
            Type = NuzlockeRuleEventType.PokemonBoxed,
            OccurredAt = DateTimeOffset.Now,
            SpeciesId = pokemon.Species,
            SpeciesName = pokemon.SpeciesName,
            Nickname = pokemon.Nickname,
            LocationName = _locationMapper.GetLocationName(pokemon.LocationMet)
                           ?? $"Unbekannter Fangort ({pokemon.LocationMet})",
            LocationId = pokemon.LocationMet > 0 ? pokemon.LocationMet : null,
            Pid = pokemon.Pid,
            IsShiny = pokemon.IsShiny
        };

    public void PublishPartnerPokemonKnockedOut(
        string partnerPlayerName,
        int partnerSpeciesId,
        string partnerSpeciesName,
        string? partnerNickname,
        string locationName,
        int? linkedSpeciesId,
        string? linkedSpeciesName,
        string? linkedNickname)
    {
        Publish(new NuzlockeRuleEvent
        {
            Type = NuzlockeRuleEventType.PartnerPokemonKnockedOut,
            OccurredAt = DateTimeOffset.Now,
            SpeciesId = partnerSpeciesId,
            SpeciesName = partnerSpeciesName,
            Nickname = partnerNickname,
            LocationName = locationName,
            PartnerPlayerName = partnerPlayerName,
            LinkedSpeciesId = linkedSpeciesId,
            LinkedSpeciesName = linkedSpeciesName,
            LinkedNickname = linkedNickname
        });
    }

    public void PublishPartnerPokemonBoxed(
        string partnerPlayerName,
        int partnerSpeciesId,
        string partnerSpeciesName,
        string? partnerNickname,
        string locationName,
        int? linkedSpeciesId,
        string? linkedSpeciesName,
        string? linkedNickname)
    {
        Publish(new NuzlockeRuleEvent
        {
            Type = NuzlockeRuleEventType.PartnerPokemonBoxed,
            OccurredAt = DateTimeOffset.Now,
            SpeciesId = partnerSpeciesId,
            SpeciesName = partnerSpeciesName,
            Nickname = partnerNickname,
            LocationName = locationName,
            PartnerPlayerName = partnerPlayerName,
            LinkedSpeciesId = linkedSpeciesId,
            LinkedSpeciesName = linkedSpeciesName,
            LinkedNickname = linkedNickname
        });
    }

    private NuzlockeRuleEvent? TryResolveCaughtPokemon(PartyPokemon pokemon)
    {
        lock (_catchSync)
        {
            if (_activeEncounter is null ||
                !_activeEncounter.IsCatchable ||
                _activeEncounter.WasCaught)
            {
                return null;
            }

            var pidMatches = _activeEncounter.Pokemon.Pid != 0 &&
                             pokemon.Pid != 0 &&
                             _activeEncounter.Pokemon.Pid == pokemon.Pid;
            var fallbackMatches = _activeEncounter.Pokemon.Pid == 0 &&
                                  pokemon.Species == _activeEncounter.Pokemon.SpeciesId;

            if (!pidMatches && !fallbackMatches)
                return null;

            var encounter = _activeEncounter;
            encounter.WasCaught = true;
            CancelPendingCatchFailureLocked();
            _activeEncounter = null;

            return new NuzlockeRuleEvent
            {
                Type = NuzlockeRuleEventType.CatchSucceeded,
                OccurredAt = DateTimeOffset.Now,
                SpeciesId = pokemon.Species,
                SpeciesName = pokemon.SpeciesName,
                Nickname = pokemon.Nickname,
                LocationName = encounter.LocationName,
                LocationId = encounter.LocationId,
                Pid = pokemon.Pid,
                IsShiny = pokemon.IsShiny,
                IsFirstEncounter = encounter.IsFirstEncounter
            };
        }
    }

    private void ScheduleCatchFailure(ActiveCatchEncounter encounter)
    {
        CancellationTokenSource cancellation;

        lock (_catchSync)
        {
            if (!ReferenceEquals(_activeEncounter, encounter) ||
                encounter.WasCaught ||
                !encounter.BattleEnded)
            {
                return;
            }

            CancelPendingCatchFailureLocked();
            cancellation = new CancellationTokenSource();
            _pendingCatchFailure = cancellation;
        }

        _ = ResolveCatchFailureAfterGracePeriodAsync(encounter, cancellation);
    }

    private async Task ResolveCatchFailureAfterGracePeriodAsync(
        ActiveCatchEncounter encounter,
        CancellationTokenSource cancellation)
    {
        try
        {
            await Task.Delay(CatchResolutionGracePeriod, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
            cancellation.Dispose();
            return;
        }

        NuzlockeRuleEvent? failureEvent = null;

        lock (_catchSync)
        {
            if (ReferenceEquals(_pendingCatchFailure, cancellation))
                _pendingCatchFailure = null;

            if (ReferenceEquals(_activeEncounter, encounter) &&
                encounter.BattleEnded &&
                encounter.IsCatchable &&
                !encounter.WasCaught)
            {
                _activeEncounter = null;
                failureEvent = CreateCatchFailedEvent(encounter);
            }
        }

        cancellation.Dispose();

        if (failureEvent is not null)
            Publish(failureEvent);
    }

    private void CancelPendingCatchFailureLocked()
    {
        _pendingCatchFailure?.Cancel();
        _pendingCatchFailure = null;
    }

    private static NuzlockeRuleEvent CreateCatchFailedEvent(ActiveCatchEncounter encounter) =>
        new()
        {
            Type = NuzlockeRuleEventType.CatchFailed,
            OccurredAt = DateTimeOffset.Now,
            SpeciesId = encounter.Pokemon.SpeciesId,
            SpeciesName = encounter.Pokemon.SpeciesName,
            Nickname = encounter.Pokemon.Nickname,
            LocationName = encounter.LocationName,
            LocationId = encounter.LocationId,
            Pid = encounter.Pokemon.Pid,
            IsShiny = encounter.Pokemon.IsShiny,
            IsFirstEncounter = encounter.IsFirstEncounter
        };

    private static string ResolveLiveLocationName(string stateLocationName, int? locationId)
    {
        if (!string.IsNullOrWhiteSpace(stateLocationName) &&
            !stateLocationName.Contains("pending", StringComparison.OrdinalIgnoreCase))
        {
            return stateLocationName.Trim();
        }

        return locationId is > 0
            ? $"Unbekannter Ort ({locationId.Value})"
            : "Unbekannter Ort";
    }

    private static bool IsUnknownLiveLocation(string locationName) =>
        locationName.StartsWith("Unbekannter Ort", StringComparison.OrdinalIgnoreCase);

    private static string NormalizeEncounterLocation(string value) =>
        new(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private void Publish(NuzlockeRuleEvent ruleEvent)
    {
        EventOccurred?.Invoke(this, ruleEvent);

        var name = FormatPokemon(ruleEvent.SpeciesName, ruleEvent.Nickname);

        switch (ruleEvent.Type)
        {
            case NuzlockeRuleEventType.PokemonKnockedOut:
                Console.WriteLine($"Nuzlocke-Event: {name} ist K.O. gegangen.");
                break;

            case NuzlockeRuleEventType.PokemonBoxed:
                Console.WriteLine($"Nuzlocke-Event: {name} wurde in die Box gelegt.");
                break;

            case NuzlockeRuleEventType.PartnerPokemonKnockedOut:
                var linkedName = string.IsNullOrWhiteSpace(ruleEvent.LinkedSpeciesName)
                    ? "Das verbundene Pokémon"
                    : FormatPokemon(ruleEvent.LinkedSpeciesName, ruleEvent.LinkedNickname);
                Console.WriteLine(
                    $"SoulLink-Event: Partner-Pokémon {name} ist K.O. gegangen. " +
                    $"{linkedName} muss abgelegt werden.");
                break;

            case NuzlockeRuleEventType.PartnerPokemonBoxed:
                var linkedBoxName = string.IsNullOrWhiteSpace(ruleEvent.LinkedSpeciesName)
                    ? "Das verbundene Pokémon"
                    : FormatPokemon(ruleEvent.LinkedSpeciesName, ruleEvent.LinkedNickname);
                Console.WriteLine(
                    $"SoulLink-Event: Partner hat {name} eingeboxt. " +
                    $"{linkedBoxName} muss ebenfalls in die Box.");
                break;

            case NuzlockeRuleEventType.CatchableEncounter:
                var reason = ruleEvent.IsShiny && ruleEvent.IsFirstEncounter
                    ? "erster Encounter und Shiny"
                    : ruleEvent.IsShiny
                        ? "Shiny"
                        : "erster Encounter";
                Console.WriteLine(
                    $"Nuzlocke-Event: {name} ist in {ruleEvent.LocationName} fangbar ({reason}).");
                break;

            case NuzlockeRuleEventType.CatchSucceeded:
                Console.WriteLine(
                    $"Nuzlocke-Event: {name} wurde in {ruleEvent.LocationName} gefangen.");
                break;

            case NuzlockeRuleEventType.CatchFailed:
                Console.WriteLine(
                    $"Nuzlocke-Event: Fang von {name} in {ruleEvent.LocationName} ist missglückt.");
                break;
        }
    }

    private static string FormatPokemon(string species, string? nickname) =>
        string.IsNullOrWhiteSpace(nickname)
            ? species
            : $"{nickname} ({species})";

    private static bool IsUsablePokemon(PartyPokemon? pokemon) =>
        pokemon is not null && pokemon.Species > 0 && !pokemon.IsEgg;

    private static long GetPokemonIdentity(PartyPokemon pokemon) =>
        pokemon.Pid != 0
            ? pokemon.Pid
            : BuildFallbackIdentity(pokemon);

    private static long BuildFallbackIdentity(PartyPokemon pokemon) =>
        -Math.Abs(HashCode.Combine(
            pokemon.Species,
            pokemon.OriginalTrainerId,
            pokemon.OriginalTrainerSecretId,
            pokemon.LocationMet));

    private sealed class PendingBoxTransfer
    {
        public PendingBoxTransfer(PartyPokemon pokemon, DateTimeOffset removedAt)
        {
            Pokemon = pokemon;
            RemovedAt = removedAt;
        }

        public PartyPokemon Pokemon { get; }
        public DateTimeOffset RemovedAt { get; }
    }

    private sealed class ActiveCatchEncounter
    {
        public ActiveCatchEncounter(
            LivePokemonState pokemon,
            string locationName,
            int? locationId,
            bool isFirstEncounter,
            bool isCatchable)
        {
            Pokemon = pokemon;
            LocationName = locationName;
            LocationId = locationId;
            IsFirstEncounter = isFirstEncounter;
            IsCatchable = isCatchable;
        }

        public LivePokemonState Pokemon { get; }
        public string LocationName { get; }
        public int? LocationId { get; }
        public bool IsFirstEncounter { get; }
        public bool IsCatchable { get; }
        public bool BattleEnded { get; set; }
        public bool WasCaught { get; set; }
    }
}
