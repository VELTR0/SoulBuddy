using SoulBuddy.Models;
using SoulBuddy.Services;

namespace SoulBuddy.Sources;

public sealed class NuzlockeRuleEventSource
{
    private readonly LocationMapper _locationMapper;
    private readonly Dictionary<long, int> _lastHpByPid = [];
    private readonly HashSet<string> _encounteredLocations = new(StringComparer.OrdinalIgnoreCase);
    private ActiveCatchEncounter? _activeEncounter;
    private bool _wasInBattle;

    public NuzlockeRuleEventSource(LocationMapper locationMapper)
    {
        _locationMapper = locationMapper;
    }

    public event EventHandler<NuzlockeRuleEvent>? EventOccurred;

    public void ObservePlayerState(PlayerLiveState state)
    {
        if (_wasInBattle && !state.InBattle && _activeEncounter is not null)
        {
            if (_activeEncounter.IsCatchable && !_activeEncounter.WasCaught)
            {
                Publish(new NuzlockeRuleEvent
                {
                    Type = NuzlockeRuleEventType.CatchFailed,
                    OccurredAt = DateTimeOffset.Now,
                    SpeciesId = _activeEncounter.Pokemon.SpeciesId,
                    SpeciesName = _activeEncounter.Pokemon.SpeciesName,
                    Nickname = _activeEncounter.Pokemon.Nickname,
                    LocationName = _activeEncounter.LocationName,
                    LocationId = _activeEncounter.LocationId,
                    Pid = _activeEncounter.Pokemon.Pid,
                    IsShiny = _activeEncounter.Pokemon.IsShiny,
                    IsFirstEncounter = _activeEncounter.IsFirstEncounter
                });
            }

            _activeEncounter = null;
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
                    Publish(new NuzlockeRuleEvent
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
                    });
                }
            }
        }

        _wasInBattle = state.InBattle;
    }

    public void ObservePokemonUpdate(IEnumerable<PartySlot> slots)
    {
        foreach (var slot in slots)
        {
            var pokemon = slot.Pokemon;
            if (pokemon is null || pokemon.Species <= 0 || pokemon.IsEgg)
                continue;

            var identity = pokemon.Pid != 0
                ? pokemon.Pid
                : BuildFallbackIdentity(pokemon);

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

            if (_activeEncounter is null ||
                !_activeEncounter.IsCatchable ||
                _activeEncounter.WasCaught)
            {
                continue;
            }

            var pidMatches = _activeEncounter.Pokemon.Pid != 0 &&
                             pokemon.Pid != 0 &&
                             _activeEncounter.Pokemon.Pid == pokemon.Pid;
            var fallbackMatches = _activeEncounter.Pokemon.Pid == 0 &&
                                  pokemon.Species == _activeEncounter.Pokemon.SpeciesId;

            if (!pidMatches && !fallbackMatches)
                continue;

            _activeEncounter.WasCaught = true;
            Publish(new NuzlockeRuleEvent
            {
                Type = NuzlockeRuleEventType.CatchSucceeded,
                OccurredAt = DateTimeOffset.Now,
                SpeciesId = pokemon.Species,
                SpeciesName = pokemon.SpeciesName,
                Nickname = pokemon.Nickname,
                LocationName = _activeEncounter.LocationName,
                LocationId = _activeEncounter.LocationId,
                Pid = pokemon.Pid,
                IsShiny = pokemon.IsShiny,
                IsFirstEncounter = _activeEncounter.IsFirstEncounter
            });
        }
    }

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

            case NuzlockeRuleEventType.PartnerPokemonKnockedOut:
                var linkedName = string.IsNullOrWhiteSpace(ruleEvent.LinkedSpeciesName)
                    ? "Das verbundene Pokémon"
                    : FormatPokemon(ruleEvent.LinkedSpeciesName, ruleEvent.LinkedNickname);
                Console.WriteLine(
                    $"SoulLink-Event: Partner-Pokémon {name} ist K.O. gegangen. " +
                    $"{linkedName} muss abgelegt werden.");
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

    private static long BuildFallbackIdentity(PartyPokemon pokemon) =>
        -Math.Abs(HashCode.Combine(
            pokemon.Species,
            pokemon.OriginalTrainerId,
            pokemon.OriginalTrainerSecretId,
            pokemon.LocationMet));

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
        public bool WasCaught { get; set; }
    }
}
