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
    private static readonly Dictionary<Window, SoulLinkOverview> Overviews = [];
    private static readonly PokemonVisualService VisualService = new();
    private static readonly SoulLinkRegistry Registry = new();
    private static DispatcherTimer? _timer;
    private static bool _registryUpdateInProgress;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
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
        var snapshot = network?.LatestRemoteSnapshot;
        var remoteParty = snapshot?.Party ?? [];
        var connected = network?.State == SoulBuddyNetworkState.Connected;
        var allPairs = new List<SoulLinkPair>();

        foreach (var window in desktop.Windows)
        {
            var pairs = UpdateWindow(
                window,
                connected ? remoteParty : [],
                snapshot?.PlayerName ?? string.Empty);
            allPairs.AddRange(pairs);
            UpdateOverview(window, pairs, connected);
        }

        if (!_registryUpdateInProgress)
        {
            _registryUpdateInProgress = true;
            _ = SaveRegistryAsync(allPairs);
        }
    }

    private static async Task SaveRegistryAsync(IReadOnlyList<SoulLinkPair> pairs)
    {
        try
        {
            await Registry.UpdateAsync(pairs);
        }
        catch
        {
            // Die UI soll bei einem vorübergehenden Dateifehler weiterlaufen.
        }
        finally
        {
            _registryUpdateInProgress = false;
        }
    }

    private static IReadOnlyList<SoulLinkPair> UpdateWindow(
        Window window,
        IReadOnlyList<NetworkPokemonSnapshot> remoteParty,
        string partnerPlayerName)
    {
        var availablePartners = remoteParty
            .Select(pokemon => new PartnerCandidate(pokemon))
            .ToList();
        var pairs = new List<SoulLinkPair>();

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

            var texts = card
                .GetVisualDescendants()
                .OfType<TextBlock>()
                .ToArray();
            var locationText = texts
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
            var localName = texts
                .Select(text => text.Text)
                .FirstOrDefault(text =>
                    !string.IsNullOrWhiteSpace(text) &&
                    !text.StartsWith("📍", StringComparison.Ordinal) &&
                    !text.StartsWith("🔗", StringComparison.Ordinal) &&
                    !text.StartsWith("Level ", StringComparison.Ordinal) &&
                    !text.EndsWith(" KP", StringComparison.Ordinal))
                ?? "Unbekannt";
            var (localCurrentHp, localMaxHp) = ParseHp(texts);

            var match = availablePartners.FirstOrDefault(candidate =>
                NormalizeLocation(candidate.Pokemon.Location) == localKey);
            var panel = GetOrCreatePanel(card, linkText);

            if (match is null)
            {
                ShowUnlinked(panel);
                pairs.Add(new SoulLinkPair
                {
                    LocationKey = localKey,
                    LocationName = localLocation,
                    LocalPokemonName = localName,
                    LocalCurrentHp = localCurrentHp,
                    LocalMaxHp = localMaxHp,
                    PartnerPlayerName = partnerPlayerName,
                    Status = SoulLinkPairStatus.MissingPartner
                });
                continue;
            }

            availablePartners.Remove(match);
            var fainted = localCurrentHp == 0 || match.Pokemon.CurrentHp == 0;
            ShowLinked(panel, match.Pokemon, fainted);
            pairs.Add(new SoulLinkPair
            {
                LocationKey = localKey,
                LocationName = localLocation,
                LocalPokemonName = localName,
                LocalCurrentHp = localCurrentHp,
                LocalMaxHp = localMaxHp,
                PartnerSpeciesId = match.Pokemon.SpeciesId,
                PartnerPokemonName = match.Pokemon.DisplayName,
                PartnerCurrentHp = match.Pokemon.CurrentHp,
                PartnerMaxHp = match.Pokemon.MaxHp,
                PartnerPlayerName = partnerPlayerName,
                Status = fainted
                    ? SoulLinkPairStatus.Fainted
                    : SoulLinkPairStatus.Active
            });
        }

        return pairs;
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
            Width = 38,
            Height = 38,
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
            Children = { partnerLabel, partnerImage, partnerName }
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
        const string signature = "unlinked";
        if (panel.Signature == signature)
        {
            return;
        }

        panel.Signature = signature;
        panel.SpeciesId = 0;
        panel.Image.Source = null;
        panel.Label.Text = "SOULLINK";
        panel.Label.Foreground = Color("#FBBF24");
        panel.Name.Text = "Noch nicht\nverknüpft";
        panel.Name.Foreground = Color("#FDE68A");
        panel.Border.Background = Color("#2A2111");
        panel.Border.BorderBrush = Color("#D97706");
    }

    private static void ShowLinked(
        PartnerPanel panel,
        NetworkPokemonSnapshot pokemon,
        bool fainted)
    {
        var signature =
            $"{pokemon.SpeciesId}:{pokemon.DisplayName}:{pokemon.CurrentHp}:{fainted}";
        if (panel.Signature == signature)
        {
            return;
        }

        panel.Signature = signature;
        panel.Label.Text = fainted ? "KAMPFUNFÄHIG" : "VERKNÜPFT MIT";
        panel.Label.Foreground = Color(fainted ? "#FCA5A5" : "#86EFAC");
        panel.Name.Text = pokemon.DisplayName;
        panel.Name.Foreground = Color("#F8FAFC");
        panel.Border.Background = Color(fainted ? "#301717" : "#10251F");
        panel.Border.BorderBrush = Color(fainted ? "#EF4444" : "#22C55E");

        if (panel.SpeciesId != pokemon.SpeciesId)
        {
            panel.SpeciesId = pokemon.SpeciesId;
            panel.Image.Source = null;
            _ = LoadPartnerSpriteAsync(panel, pokemon.SpeciesId);
        }
    }

    private static void UpdateOverview(
        Window window,
        IReadOnlyList<SoulLinkPair> pairs,
        bool connected)
    {
        var overview = GetOrCreateOverview(window);
        if (overview is null)
        {
            return;
        }

        var signature = connected + ":" + string.Join("|", pairs.Select(pair =>
            $"{pair.LocationKey}:{pair.LocalPokemonName}:" +
            $"{pair.PartnerPokemonName}:{pair.Status}"));
        if (overview.Signature == signature)
        {
            return;
        }

        overview.Signature = signature;
        overview.Panel.Children.Clear();

        if (!connected)
        {
            overview.Panel.Children.Add(OverviewText(
                "Offline · Verbinde dich mit einem Mitspieler, um SoulLinks zu sehen.",
                "#94A3B8",
                11));
            return;
        }

        if (pairs.Count == 0)
        {
            overview.Panel.Children.Add(OverviewText(
                "Noch keine Team-Pokémon erkannt.",
                "#94A3B8",
                11));
            return;
        }

        foreach (var pair in pairs)
        {
            var status = pair.Status switch
            {
                SoulLinkPairStatus.Active => ("● AKTIV", "#86EFAC", "#10251F", "#22C55E"),
                SoulLinkPairStatus.Fainted => ("● KAMPFUNFÄHIG", "#FCA5A5", "#301717", "#EF4444"),
                _ => ("● PARTNER FEHLT", "#FDE68A", "#2A2111", "#D97706")
            };
            var partner = string.IsNullOrWhiteSpace(pair.PartnerPokemonName)
                ? "Noch kein Partner"
                : pair.PartnerPokemonName;
            var stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(OverviewText(pair.LocationName, "#93C5FD", 10, FontWeight.Bold));
            stack.Children.Add(OverviewText(
                $"{pair.LocalPokemonName}  ↔  {partner}",
                "#F8FAFC",
                12,
                FontWeight.SemiBold));
            stack.Children.Add(OverviewText(status.Item1, status.Item2, 9, FontWeight.Bold));
            overview.Panel.Children.Add(new Border
            {
                Background = Color(status.Item3),
                BorderBrush = Color(status.Item4),
                BorderThickness = new Thickness(1),
                CornerRadius = new CornerRadius(8),
                Padding = new Thickness(9, 7),
                Child = stack
            });
        }
    }

    private static SoulLinkOverview? GetOrCreateOverview(Window window)
    {
        if (Overviews.TryGetValue(window, out var existing))
        {
            return existing;
        }

        var tabs = window
            .GetVisualDescendants()
            .OfType<TabControl>()
            .FirstOrDefault();
        if (tabs is null)
        {
            return null;
        }

        var panel = new StackPanel
        {
            Spacing = 7,
            Margin = new Thickness(2)
        };
        tabs.Items.Add(new TabItem
        {
            Header = "SoulLinks",
            Content = new ScrollViewer
            {
                Content = panel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        });

        var overview = new SoulLinkOverview(panel);
        Overviews[window] = overview;
        window.Closed += (_, _) => Overviews.Remove(window);
        return overview;
    }

    private static TextBlock OverviewText(
        string value,
        string color,
        double size,
        FontWeight? weight = null) => new()
    {
        Text = value,
        Foreground = Color(color),
        FontSize = size,
        FontWeight = weight ?? FontWeight.Normal,
        TextWrapping = TextWrapping.Wrap
    };

    private static (int Current, int Max) ParseHp(IEnumerable<TextBlock> texts)
    {
        var hpText = texts
            .Select(text => text.Text)
            .FirstOrDefault(value =>
                !string.IsNullOrWhiteSpace(value) &&
                value.EndsWith(" KP", StringComparison.Ordinal) &&
                value.Contains('/'));
        if (string.IsNullOrWhiteSpace(hpText))
        {
            return (-1, -1);
        }

        var value = hpText.Replace(" KP", string.Empty);
        var parts = value.Split('/', StringSplitOptions.TrimEntries);
        return parts.Length == 2 &&
               int.TryParse(parts[0], out var current) &&
               int.TryParse(parts[1], out var max)
            ? (current, max)
            : (-1, -1);
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
        public string Signature { get; set; } = string.Empty;
    }

    private sealed class SoulLinkOverview(StackPanel panel)
    {
        public StackPanel Panel { get; } = panel;
        public string Signature { get; set; } = string.Empty;
    }
}
