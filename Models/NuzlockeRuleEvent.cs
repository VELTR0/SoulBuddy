namespace SoulBuddy.Models;

public enum NuzlockeRuleEventType
{
    PokemonKnockedOut,
    PartnerPokemonKnockedOut,
    CatchableEncounter,
    CatchSucceeded,
    CatchFailed
}

public sealed class NuzlockeRuleEvent : EventArgs
{
    public required NuzlockeRuleEventType Type { get; init; }
    public required DateTimeOffset OccurredAt { get; init; }
    public required int SpeciesId { get; init; }
    public required string SpeciesName { get; init; }
    public string? Nickname { get; init; }
    public string LocationName { get; init; } = "Unbekannter Ort";
    public int? LocationId { get; init; }
    public long Pid { get; init; }
    public bool IsShiny { get; init; }
    public bool IsFirstEncounter { get; init; }
    public string? PartnerPlayerName { get; init; }
    public int? LinkedSpeciesId { get; init; }
    public string? LinkedSpeciesName { get; init; }
    public string? LinkedNickname { get; init; }
}
