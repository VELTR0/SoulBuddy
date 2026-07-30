namespace SoulBuddy.ViewModels;

public sealed class PokemonCardViewModel
{
    public required string DisplayName { get; init; }
    public required string Species { get; init; }
    public required string Subtitle { get; init; }
    public required string DetailsTitle { get; init; }
    public required string DetailsText { get; init; }
    public int Level { get; init; }
    public int CurrentHp { get; init; }
    public int MaxHp { get; init; }

    public string NameLine => string.Equals(
        DisplayName,
        Species,
        StringComparison.OrdinalIgnoreCase)
        ? DisplayName
        : $"{DisplayName} · {Species}";

    public string LevelText => $"Level {Level}";

    public string HpText => MaxHp > 0
        ? $"{CurrentHp} / {MaxHp} KP"
        : "KP unbekannt";

    public double HpPercentage => MaxHp <= 0
        ? 0
        : Math.Clamp(CurrentHp * 100d / MaxHp, 0, 100);
}
