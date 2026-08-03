using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
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
        var connected = network?.State == SoulBuddyNetworkState.Connected;
        var remoteParty = connected ? snapshot?.Party ?? [] : [];
        var allPairs = new List<SoulLinkPair>();

        foreach (var window in desktop.Windows)
        {
            var pairs = UpdateWindow(
                window,
                remoteParty,
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
            // Ein vorübergehender Dateifehler darf die Oberfläche nicht stoppen.
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
        var cards = FindPartyCards(window);
        if (cards.Count == 0)
        {
            return [];
        }

        var localEntries = cards
            .Select((card, index) => ReadLocalCard(card, index))
            .Where(entry => entry is not null)
            .Cast<LocalCard>()
            .ToArray();

        var localLocationKeys = localEntries
            .Select(entry => NormalizeLocation(entry.Location))
            .Where(key => key.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var hasAnyLocationOverlap = remoteParty.Any(remote =>
            localLocationKeys.Contains(NormalizeLocation(remote.Location)));

        var available = remoteParty
            .Select((pokemon, index) => new PartnerCandidate(pokemon, index))
            .ToList();
        var pairs = new List<SoulLinkPair>();

        foreach (var local in localEntries)
        {
            var panel = GetOrCreatePanel(local.Card, local.LinkText);
            if (panel is null)
            {
                continue;
            }

            var localKey = NormalizeLocation(local.Location);
            var match = available.FirstOrDefault(candidate =>
                NormalizeLocation(candidate.Pokemon.Location) == localKey);

            // Beim gleichen Savegame sind Name, Spezies und Level identisch.
            // Diese Identitätsprüfung fängt auch abweichende Ortsbezeichnungen ab.
            match ??= available.FirstOrDefault(candidate =>
                IsSamePokemon(local, candidate.Pokemon));

            // Nur wenn überhaupt kein Fangort zwischen den Teams übereinstimmt,
            // verwenden wir die Teamposition als letzte Kompatibilitätslösung.
            if (match is null &&
                !hasAnyLocationOverlap &&
                local.Index < remoteParty.Count)
            {
                match = available.FirstOrDefault(candidate =>
                    candidate.Index == local.Index);
            }

            if (match is null)
            {
                ShowUnlinked(panel);
                pairs.Add(CreatePair(local, null, partnerPlayerName));
                continue;
            }

            available.Remove(match);
            var fainted = local.CurrentHp == 0 || match.Pokemon.CurrentHp == 0;
            ShowLinked(panel, match.Pokemon, fainted);
            pairs.Add(CreatePair(local, match.Pokemon, partnerPlayerName));
        }

        return pairs;
    }

    private static IReadOnlyList<Button> FindPartyCards(Window window)
    {
        var header = window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text => string.Equals(
                text.Text,
                "Aktuelles Team",
                StringComparison.Ordinal));

        var section = header?
            .GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(grid =>
                grid.RowDefinitions.Count == 2 &&
                grid.Children.OfType<Grid>().Any(child => Grid.GetRow(child) == 1));
        var partyGrid = section?.Children
            .OfType<Grid>()
            .FirstOrDefault(child => Grid.GetRow(child) == 1);

        return partyGrid?.Children
            .OfType<Button>()
            .OrderBy(card => Grid.GetRow(card) * 2 + Grid.GetColumn(card))
            .ToArray() ?? [];
    }

    private static LocalCard? ReadLocalCard(Button card, int index)
    {
        var texts = card
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .ToArray();
        var locationText = texts.FirstOrDefault(text =>
            text.Text?.StartsWith("📍", StringComparison.Ordinal) == true);
        if (locationText?.Text is not { } rawLocation)
        {
            return null;
        }

        var linkText = texts.FirstOrDefault(text =>
            text.Text?.StartsWith("🔗", StringComparison.Ordinal) == true);
        var name = texts
            .Select(text => text.Text)
            .FirstOrDefault(value => IsPokemonNameText(value))
            ?? "Unbekannt";
        var level = ParseLevel(texts);
        var (currentHp, maxHp) = ParseHp(texts);

        return new LocalCard(
            card,
            linkText,
            index,
            rawLocation[2..].Trim(),
            name,
            level,
            currentHp,
            maxHp);
    }

    private static bool IsPokemonNameText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            return false;
        }

        return !value.StartsWith("📍", StringComparison.Ordinal) &&
               !value.StartsWith("🔗", StringComparison.Ordinal) &&
               !value.StartsWith("Level ", StringComparison.Ordinal) &&
               !value.EndsWith(" KP", StringComparison.Ordinal) &&
               value is not "SOULLINK" and
               not "VERKNÜPFT MIT" and
               not "KAMPFUNFÄHIG" and
               not "Noch nicht\nverknüpft";
    }

    private static bool IsSamePokemon(
        LocalCard local,
        NetworkPokemonSnapshot remote)
    {
        if (local.Level > 0 && remote.Level != local.Level)
        {
            return false;
        }

        var localName = NormalizeName(local.Name);
        var displayName = NormalizeName(remote.DisplayName);
        var speciesName = NormalizeName(remote.SpeciesName);

        return localName == displayName ||
               localName == speciesName ||
               localName.StartsWith(displayName, StringComparison.Ordinal) ||
               localName.EndsWith(speciesName, StringComparison.Ordinal);
    }

    private static SoulLinkPair CreatePair(
        LocalCard local,
        NetworkPokemonSnapshot? partner,
        string partnerPlayerName)
    {
        var fainted = partner is not null &&
                      (local.CurrentHp == 0 || partner.CurrentHp == 0);
        return new SoulLinkPair
        {
            LocationKey = NormalizeLocation(local.Location),
            LocationName = local.Location,
            LocalPokemonName = local.Name,
            LocalCurrentHp = local.CurrentHp,
            LocalMaxHp = local.MaxHp,
            PartnerSpeciesId = partner?.SpeciesId,
            PartnerPokemonName = partner?.DisplayName ?? string.Empty,
            PartnerCurrentHp = partner?.CurrentHp,
            PartnerMaxHp = partner?.MaxHp,
            PartnerPlayerName = partnerPlayerName,
            Status = partner is null
                ? SoulLinkPairStatus.MissingPartner
                : fainted
                    ? SoulLinkPairStatus.Fainted
                    : SoulLinkPairStatus.Active
        };
    }

    private static PartnerPanel? GetOrCreatePanel(
        Button card,
        TextBlock? oldLinkText)
    {
        if (Panels.TryGetValue(card, out var existing))
        {
            return existing;
        }

        if (oldLinkText is null || card.Content is not Control originalContent)
        {
            return null;
        }

        oldLinkText.IsVisible = false;

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
            Text = "SOULLINK",
            FontSize = 7,
            FontWeight = FontWeight.Bold,
            Foreground = Color("#FBBF24"),
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
            Background = Color("#2A2111"),
            BorderBrush = Color("#D97706"),
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
        ShowUnlinked(panel);
        return panel;
    }

    private static void ShowUnlinked(PartnerPanel panel)
    {
        if (panel.Signature == "unlinked")
        {
            return;
        }

        panel.Signature = "unlinked";
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

        foreach (var pair in pairs)
        {
            var status = pair.Status switch
            {
                SoulLinkPairStatus.Active =>
                    ("● AKTIV", "#86EFAC", "#10251F", "#22C55E"),
                SoulLinkPairStatus.Fainted =>
                    ("● KAMPFUNFÄHIG", "#FCA5A5", "#301717", "#EF4444"),
                _ =>
                    ("● PARTNER FEHLT", "#FDE68A", "#2A2111", "#D97706")
            };
            var partner = string.IsNullOrWhiteSpace(pair.PartnerPokemonName)
                ? "Noch kein Partner"
                : pair.PartnerPokemonName;
            var stack = new StackPanel { Spacing = 3 };
            stack.Children.Add(OverviewText(
                pair.LocationName,
                "#93C5FD",
                10,
                FontWeight.Bold));
            stack.Children.Add(OverviewText(
                $"{pair.LocalPokemonName}  ↔  {partner}",
                "#F8FAFC",
                12,
                FontWeight.SemiBold));
            stack.Children.Add(OverviewText(
                status.Item1,
                status.Item2,
                9,
                FontWeight.Bold));
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

        var tabs = window.GetVisualDescendants().OfType<TabControl>().FirstOrDefault();
        if (tabs is null)
        {
            return null;
        }

        var panel = new StackPanel { Spacing = 7, Margin = new Thickness(2) };
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

    private static int ParseLevel(IEnumerable<TextBlock> texts)
    {
        var value = texts.Select(text => text.Text).FirstOrDefault(text =>
            text?.StartsWith("Level ", StringComparison.Ordinal) == true);
        return value is not null && int.TryParse(value[6..], out var level)
            ? level
            : 0;
    }

    private static (int Current, int Max) ParseHp(IEnumerable<TextBlock> texts)
    {
        var hpText = texts.Select(text => text.Text).FirstOrDefault(value =>
            !string.IsNullOrWhiteSpace(value) &&
            value.EndsWith(" KP", StringComparison.Ordinal) &&
            value.Contains('/'));
        if (string.IsNullOrWhiteSpace(hpText))
        {
            return (-1, -1);
        }

        var parts = hpText.Replace(" KP", string.Empty)
            .Split('/', StringSplitOptions.TrimEntries);
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
        var normalized = new string(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());
        return normalized switch
        {
            "starter" or "newborkia" or "newbarktown" => "starter",
            _ => normalized
        };
    }

    private static string NormalizeName(string value) => new(value
        .Trim()
        .ToLowerInvariant()
        .Where(char.IsLetterOrDigit)
        .ToArray());

    private static SolidColorBrush Color(string value) =>
        new(Avalonia.Media.Color.Parse(value));

    private sealed record PartnerCandidate(
        NetworkPokemonSnapshot Pokemon,
        int Index);

    private sealed record LocalCard(
        Button Card,
        TextBlock? LinkText,
        int Index,
        string Location,
        string Name,
        int Level,
        int CurrentHp,
        int MaxHp);

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
