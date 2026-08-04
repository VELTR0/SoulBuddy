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

    // These optional values allow explicit links later (for example when
    // Phase 4 introduces manual corrections). Until then the current remote
    // snapshot is resolved deterministically by location and identity.
    public bool HasExplicitSoulLink { get; init; }
    public string ExplicitLinkedDisplayName { get; init; } = string.Empty;
    public string ExplicitLinkedSpecies { get; init; } = string.Empty;
    public int ExplicitLinkedSpeciesId { get; init; }
    public int ExplicitLinkedCurrentHp { get; init; } = -1;

    private NetworkPokemonSnapshot? ResolvedSoulLink
    {
        get
        {
            var network = SoulBuddyNetworkService.Current;
            if (network?.State != SoulBuddyNetworkState.Connected)
            {
                return null;
            }

            var remoteParty = network.LatestRemoteSnapshot?.Party;
            if (remoteParty is null || remoteParty.Count == 0)
            {
                return null;
            }

            var location = NormalizeLocation(Subtitle);
            var byLocation = remoteParty.FirstOrDefault(remote =>
                location.Length > 0 &&
                NormalizeLocation(remote.Location) == location);
            if (byLocation is not null)
            {
                return byLocation;
            }

            var display = Normalize(DisplayName);
            var species = Normalize(Species);
            return remoteParty.FirstOrDefault(remote =>
                (Level <= 0 || remote.Level == Level) &&
                (display == Normalize(remote.DisplayName) ||
                 display == Normalize(remote.SpeciesName) ||
                 species == Normalize(remote.DisplayName) ||
                 species == Normalize(remote.SpeciesName)));
        }
    }

    public bool IsSoulLinked => HasExplicitSoulLink || ResolvedSoulLink is not null;

    public string LinkedDisplayName => HasExplicitSoulLink
        ? ExplicitLinkedDisplayName
        : ResolvedSoulLink?.DisplayName ?? string.Empty;

    public string LinkedSpecies => HasExplicitSoulLink
        ? ExplicitLinkedSpecies
        : ResolvedSoulLink?.SpeciesName ?? string.Empty;

    public int LinkedSpeciesId => HasExplicitSoulLink
        ? ExplicitLinkedSpeciesId
        : ResolvedSoulLink?.SpeciesId ?? 0;

    public int LinkedCurrentHp => HasExplicitSoulLink
        ? ExplicitLinkedCurrentHp
        : ResolvedSoulLink?.CurrentHp ?? -1;

    public bool LinkedIsFainted => IsSoulLinked &&
                                   (CurrentHp == 0 || LinkedCurrentHp == 0);

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