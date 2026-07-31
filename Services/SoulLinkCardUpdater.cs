using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoulBuddy.Models;

namespace SoulBuddy.Services;

internal static class SoulLinkCardUpdater
{
    private static readonly Dictionary<Button, PartnerPanel> Panels = [];
    private static readonly PokemonVisualService VisualService = new();
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
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var network = SoulBuddyNetworkService.Current;
        var remoteParty = network?.LatestRemoteSnapshot?.Party ?? [];
        var connected = network?.State == SoulBuddyNetworkState.Connected;

        foreach (var window in desktop.Windows)
        {
            UpdateWindow(window, connected ? remoteParty : []);
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

            var panel = GetOrCreatePanel(card, linkText);

            if (match is null)
            {
                ShowUnlinked(panel);
                continue;
            }

            availablePartners.Remove(match);
            ShowLinked(panel, match.Pokemon);
        }
    }

    private static PartnerPanel GetOrCreatePanel(
        Button card,
        TextBlock oldLinkText)
    {
        if (Panels.TryGetValue(card, out var existing))
        {
            return existing;
        }

        oldLinkText.IsVisible = false;

        if (card.Content is not Control originalContent)
        {
            throw new InvalidOperationException(
                "Die Pokémon-Karte besitzt keinen darstellbaren Inhalt.");
        }

        var partnerImage = new Image
        {
            Width = 34,
            Height = 34,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        };

        var partnerName = new TextBlock
        {
            FontSize = 8,
            FontWeight = FontWeight.SemiBold,
            Foreground = Color("#E2E8F0"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var partnerLabel = new TextBlock
        {
            Text = "VERKNÜPFT MIT",
            FontSize = 7,
            FontWeight = FontWeight.Bold,
            Foreground = Color("#86EFAC"),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };

        var stack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children =
            {
                partnerLabel,
                partnerImage,
                partnerName
            }
        };

        var partnerBorder = new Border
        {
            Background = Color("#10251F"),
            BorderBrush = Color("#22C55E"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(3, 2),
            MinWidth = 0,
            Child = stack
        };

        var outer = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("3*,1*"),
            ColumnSpacing = 5,
            MinWidth = 0
        };

        Grid.SetColumn(originalContent, 0);
        outer.Children.Add(originalContent);
        Grid.SetColumn(partnerBorder, 1);
        outer.Children.Add(partnerBorder);
        card.Content = outer;

        var panel = new PartnerPanel(
            partnerBorder,
            partnerLabel,
            partnerImage,
            partnerName);
        Panels[card] = panel;
        return panel;
    }

    private static void ShowUnlinked(PartnerPanel panel)
    {
        panel.SpeciesId = 0;
        panel.Image.Source = null;
        panel.Label.Text = "SOULLINK";
        panel.Label.Foreground = Color("#94A3B8");
        panel.Name.Text = "Noch nicht\nverknüpft";
        panel.Name.Foreground = Color("#64748B");
        panel.Border.Background = Color("#111827");
        panel.Border.BorderBrush = Color("#334155");
    }

    private static void ShowLinked(
        PartnerPanel panel,
        NetworkPokemonSnapshot pokemon)
    {
        panel.Label.Text = "VERKNÜPFT MIT";
        panel.Label.Foreground = Color("#86EFAC");
        panel.Name.Text = pokemon.DisplayName;
        panel.Name.Foreground = Color("#F8FAFC");
        panel.Border.Background = Color("#10251F");
        panel.Border.BorderBrush = Color("#22C55E");

        if (panel.SpeciesId == pokemon.SpeciesId)
        {
            return;
        }

        panel.SpeciesId = pokemon.SpeciesId;
        panel.Image.Source = null;
        _ = LoadPartnerSpriteAsync(panel, pokemon.SpeciesId);
    }

    private static async Task LoadPartnerSpriteAsync(
        PartnerPanel panel,
        int speciesId)
    {
        var visual = await VisualService.GetAsync(speciesId, false);

        if (panel.SpeciesId == speciesId && visual.Sprite is not null)
        {
            panel.Image.Source = visual.Sprite;
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

    private static SolidColorBrush Color(string value) =>
        new(Avalonia.Media.Color.Parse(value));

    private sealed record PartnerCandidate(NetworkPokemonSnapshot Pokemon);

    private sealed class PartnerPanel(
        Border border,
        TextBlock label,
        Image image,
        TextBlock name)
    {
        public Border Border { get; } = border;
        public TextBlock Label { get; } = label;
        public Image Image { get; } = image;
        public TextBlock Name { get; } = name;
        public int SpeciesId { get; set; }
    }
}
