namespace SoulBuddy.Models;

public sealed class SoulLinkPartnerInfo
{
    public string PlayerName { get; init; } = "Partner";
    public int SpeciesId { get; init; }
    public string? Nickname { get; init; }
    public string Location { get; init; } = string.Empty;
    public string Status { get; init; } = "alive";

    public string DisplayName => string.IsNullOrWhiteSpace(Nickname)
        ? $"Pokémon #{SpeciesId}"
        : Nickname!;

    public bool IsFainted => string.Equals(
        Status,
        "fainted",
        StringComparison.OrdinalIgnoreCase);
}