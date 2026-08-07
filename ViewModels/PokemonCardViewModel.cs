using SoulBuddy.Models;
using SoulBuddy.Services;

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
    public SoulLinkPartnerInfo? SoullockePartnerLink { get; init; }

    private NetworkPokemonSnapshot? ResolvedNetworkSoulLink
    {
        get
        {
            var network = SoulBuddyNetworkService.Current;
            if (network?.State != SoulBuddyNetworkState.Connected)
                return null;

            var remoteParty = network.LatestRemoteSnapshot?.Party;
            if (remoteParty is null || remoteParty.Count == 0)
                return null;

            var location = NormalizeLocation(Subtitle);
            if (location.Length == 0)
                return null;

            return remoteParty.FirstOrDefault(remote =>
                NormalizeLocation(remote.Location) == location);
        }
    }

    public bool IsSoulLinked =>
        SoullockePartnerLink is not null ||
        ResolvedNetworkSoulLink is not null;

    public string LinkedDisplayName =>
        SoullockePartnerLink?.DisplayName ??
        ResolvedNetworkSoulLink?.DisplayName ??
        string.Empty;

    public string LinkedSpecies =>
        SoullockePartnerLink is not null
            ? $"Pokémon #{SoullockePartnerLink.SpeciesId}"
            : ResolvedNetworkSoulLink?.SpeciesName ?? string.Empty;

    public int LinkedSpeciesId =>
        SoullockePartnerLink?.SpeciesId ??
        ResolvedNetworkSoulLink?.SpeciesId ??
        0;

    public int LinkedCurrentHp =>
        ResolvedNetworkSoulLink?.CurrentHp ??
        (SoullockePartnerLink?.IsFainted == true ? 0 : -1);

    public bool LinkedIsFainted => IsSoulLinked &&
                                   (CurrentHp == 0 ||
                                    SoullockePartnerLink?.IsFainted == true ||
                                    ResolvedNetworkSoulLink?.CurrentHp == 0);

    public string NameLine => string.Equals(
        DisplayName,
        Species,
        StringComparison.OrdinalIgnoreCase)
        ? DisplayName
        : $"{DisplayName} · {Species}";

    public string LinkedNameLine => string.IsNullOrWhiteSpace(LinkedDisplayName)
        ? LinkedSpecies
        : LinkedDisplayName;

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

    private static string NormalizeLocation(string value)
    {
        var normalized = Normalize(value);
        return normalized switch
        {
            "starter" or "newborkia" or "newbarktown" => "starter",
            _ => normalized
        };
    }

    private static string Normalize(string value) => new(value
        .Trim()
        .ToLowerInvariant()
        .Where(char.IsLetterOrDigit)
        .ToArray());
}
