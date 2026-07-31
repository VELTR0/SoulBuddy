namespace SoulBuddy.ViewModels;

public sealed class PokemonCardViewModel
{
    public required string DisplayName { get; init; }
    public required string Species { get; init; }
    public required string Subtitle { get; init; }
    public required string DetailsTitle { get; init; }
    public required string DetailsText { get; init; }
    public int SpeciesId { get; init; }
    public int Level { get; init; }
    public int CurrentHp { get; init; }
    public int MaxHp { get; init; }
    public string Nature { get; init; } = string.Empty;
    public string Ability { get; init; } = string.Empty;
    public string Gender { get; init; } = string.Empty;
    public string Pokeball { get; init; } = string.Empty;
    public bool IsShiny { get; init; }

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

    public string GenderSymbol => Gender switch
    {
        "Weiblich" => "♀",
        "Männlich" => "♂",
        "Geschlechtslos" => "–",
        _ => string.Empty
    };

    public string ShinySymbol => IsShiny ? "★" : string.Empty;

    public string TraitLine
    {
        get
        {
            var values = new[] { Nature, Ability, Pokeball }
                .Where(value => !string.IsNullOrWhiteSpace(value));
            return string.Join(" · ", values);
        }
    }

    public double HpPercentage => MaxHp <= 0
        ? 0
        : Math.Clamp(CurrentHp * 100d / MaxHp, 0, 100);
}