using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoulBuddy.Models;

namespace SoulBuddy.Services;

internal static class SoulLinkCardUpdater
{
    private static DispatcherTimer? _timer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _timer.Tick += (_, _) => UpdateVisiblePartyCards();
            _timer.Start();
        });
    }

    private static void UpdateVisiblePartyCards()
    {
        var network = SoulBuddyNetworkService.Current;
        var remoteParty = network?.LatestRemoteSnapshot?.Party;

        if (network?.State != SoulBuddyNetworkState.Connected ||
            remoteParty is null ||
            remoteParty.Count == 0)
        {
            return;
        }

        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (var window in desktop.Windows)
        {
            UpdateWindow(window, remoteParty);
        }
    }

    private static void UpdateWindow(
        Window window,
        IReadOnlyList<NetworkPokemonSnapshot> remoteParty)
    {
        var availablePartners = remoteParty
            .Select(pokemon => new PartnerCandidate(pokemon))
            .ToList();

        var linkTexts = window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .Where(text => text.Text is not null &&
                           text.Text.StartsWith("🔗", StringComparison.Ordinal))
            .ToArray();

        foreach (var linkText in linkTexts)
        {
            var card = linkText
                .GetVisualAncestors()
                .OfType<Button>()
                .FirstOrDefault();

            if (card is null)
            {
                continue;
            }

            var locationText = card
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .Select(text => text.Text)
                .FirstOrDefault(text =>
                    !string.IsNullOrWhiteSpace(text) &&
                    text.StartsWith("📍", StringComparison.Ordinal));

            if (string.IsNullOrWhiteSpace(locationText))
            {
                continue;
            }

            var localLocation = locationText[2..].Trim();
            var localKey = NormalizeLocation(localLocation);
            var match = availablePartners.FirstOrDefault(candidate =>
                NormalizeLocation(candidate.Pokemon.Location) == localKey);

            if (match is null)
            {
                linkText.Text = "🔗 noch nicht verknüpft";
                linkText.Foreground = Color("#FBBF24");
                continue;
            }

            availablePartners.Remove(match);
            linkText.Text =
                $"🔗 {match.Pokemon.DisplayName} · {match.Pokemon.SpeciesName}";
            linkText.Foreground = Color("#86EFAC");
        }
    }

    private static string NormalizeLocation(string value)
    {
        var normalized = value
            .Trim()
            .ToLowerInvariant()
            .Replace("📍", string.Empty)
            .Replace(" ", string.Empty)
            .Replace("-", string.Empty)
            .Replace("_", string.Empty);

        return normalized switch
        {
            "starter" => "starter",
            "newborkia" => "starter",
            "newbarktown" => "starter",
            _ => normalized
        };
    }

    private static Avalonia.Media.SolidColorBrush Color(string value) =>
        new(Avalonia.Media.Color.Parse(value));

    private sealed record PartnerCandidate(NetworkPokemonSnapshot Pokemon);
}
