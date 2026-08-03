using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoulBuddy.Models;

namespace SoulBuddy.Services;

/// <summary>
/// Keeps location-based SoulLink matching robust when two clients expose
/// different display names for the same encounter locations. Exact location
/// matching remains the default. Slot-order fallback is only applied when
/// there is no location overlap at all between both complete team snapshots.
/// </summary>
internal static class SoulLinkLocationCompatibility
{
    private static readonly PropertyInfo? LatestSnapshotProperty =
        typeof(SoulBuddyNetworkService).GetProperty(
            nameof(SoulBuddyNetworkService.LatestRemoteSnapshot));

    private static DispatcherTimer? _timer;
    private static string _lastSignature = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _timer.Tick += (_, _) => ApplyCompatibilityMapping();
            _timer.Start();
        });
    }

    private static void ApplyCompatibilityMapping()
    {
        var network = SoulBuddyNetworkService.Current;
        var snapshot = network?.LatestRemoteSnapshot;
        if (network is null ||
            snapshot is null ||
            snapshot.Party.Count == 0 ||
            Application.Current?.ApplicationLifetime is not
                IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var window = desktop.Windows.FirstOrDefault(candidate =>
            candidate.GetVisualDescendants()
                .OfType<TextBlock>()
                .Any(text => string.Equals(
                    text.Text,
                    "Aktuelles Team",
                    StringComparison.Ordinal)));
        if (window is null)
        {
            return;
        }

        var localLocations = GetLocalPartyLocations(window);
        if (localLocations.Count == 0 ||
            localLocations.Count != snapshot.Party.Count)
        {
            return;
        }

        var remoteLocations = snapshot.Party
            .Select(pokemon => pokemon.Location)
            .ToArray();
        var signature = string.Join("|", localLocations) + "::" +
                        string.Join("|", remoteLocations);
        if (signature == _lastSignature)
        {
            return;
        }
        _lastSignature = signature;

        var localKeys = localLocations
            .Select(NormalizeLocation)
            .Where(value => value.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var exactMatches = remoteLocations.Count(location =>
            localKeys.Contains(NormalizeLocation(location)));

        // Normal location matching already works; do not alter the snapshot.
        if (exactMatches > 0)
        {
            return;
        }

        var remappedParty = snapshot.Party
            .Select((pokemon, index) => CloneWithLocation(
                pokemon,
                localLocations[index]))
            .ToArray();

        var remappedSnapshot = new NetworkPlayerSnapshot
        {
            PlayerName = snapshot.PlayerName,
            Game = snapshot.Game,
            Timestamp = snapshot.Timestamp,
            StoredPokemonCount = snapshot.StoredPokemonCount,
            ActivePokemon = snapshot.ActivePokemon,
            Party = remappedParty
        };

        // The service intentionally exposes snapshots as read-only. This
        // compatibility layer replaces only the last received immutable
        // snapshot so the existing SoulLink renderer can match it normally.
        LatestSnapshotProperty?.SetValue(network, remappedSnapshot);
    }

    private static IReadOnlyList<string> GetLocalPartyLocations(Window window)
    {
        var teamHeader = window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text => string.Equals(
                text.Text,
                "Aktuelles Team",
                StringComparison.Ordinal));
        var partyPanel = teamHeader?
            .GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(grid =>
                grid.RowDefinitions.Count == 2)?
            .Children
            .OfType<Grid>()
            .FirstOrDefault(grid => Grid.GetRow(grid) == 1);

        if (partyPanel is null)
        {
            return [];
        }

        return partyPanel.Children
            .OrderBy(control => Grid.GetRow(control) * 2 + Grid.GetColumn(control))
            .Select(GetLocationFromCard)
            .Where(location => !string.IsNullOrWhiteSpace(location))
            .ToArray();
    }

    private static string GetLocationFromCard(Control card)
    {
        var text = card
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Select(item => item.Text)
            .FirstOrDefault(value =>
                !string.IsNullOrWhiteSpace(value) &&
                value.StartsWith("📍", StringComparison.Ordinal));

        return string.IsNullOrWhiteSpace(text)
            ? string.Empty
            : text[2..].Trim();
    }

    private static NetworkPokemonSnapshot CloneWithLocation(
        NetworkPokemonSnapshot pokemon,
        string location) => new()
    {
        SpeciesId = pokemon.SpeciesId,
        SpeciesName = pokemon.SpeciesName,
        Nickname = pokemon.Nickname,
        Level = pokemon.Level,
        CurrentHp = pokemon.CurrentHp,
        MaxHp = pokemon.MaxHp,
        Location = location
    };

    private static string NormalizeLocation(string value)
    {
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        return normalized switch
        {
            "starter" => "starter",
            "newborkia" => "starter",
            "newbarktown" => "starter",
            _ => normalized
        };
    }
}
