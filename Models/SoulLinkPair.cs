namespace SoulBuddy.Models;

public enum SoulLinkPairStatus
{
    Active,
    MissingPartner,
    Fainted
}

public sealed class SoulLinkPair
{
    public string LocationKey { get; init; } = string.Empty;
    public string LocationName { get; init; } = string.Empty;
    public int LocalSpeciesId { get; init; }
    public string LocalPokemonName { get; init; } = string.Empty;
    public int LocalCurrentHp { get; init; }
    public int LocalMaxHp { get; init; }
    public int? PartnerSpeciesId { get; init; }
    public string PartnerPokemonName { get; init; } = string.Empty;
    public int? PartnerCurrentHp { get; init; }
    public int? PartnerMaxHp { get; init; }
    public string PartnerPlayerName { get; init; } = string.Empty;
    public SoulLinkPairStatus Status { get; init; }
    public DateTimeOffset UpdatedAt { get; init; } = DateTimeOffset.UtcNow;
}
