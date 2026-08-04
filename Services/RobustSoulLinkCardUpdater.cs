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

/// <summary>
/// Updates the partner section of team cards independently of the surrounding
/// grid layout. The original updater relied on the old one-column party grid;
/// this updater recognizes team cards by their own contents instead.
/// </summary>
internal static class RobustSoulLinkCardUpdater
{
    private static readonly Dictionary<Button, PartnerView> Views = [];
    private static readonly PokemonVisualService VisualService = new();
    private static DispatcherTimer? _timer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _timer.Tick += (_, _) => Update();
            _timer.Start();
        });
    }

    private static void Update()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var network = SoulBuddyNetworkService.Current;
        var connected = network?.State == SoulBuddyNetworkState.Connected;
        var remoteParty = connected
            ? network?.LatestRemoteSnapshot?.Party ?? []
            : [];

        foreach (var window in desktop.Windows)
        {
            UpdateWindow(window, remoteParty);
        }
    }

    private static void UpdateWindow(
        Window window,
        IReadOnlyList<NetworkPokemonSnapshot> remoteParty)
    {
        var cards = FindTeamCards(window);
        if (cards.Count == 0)
        {
            return;
        }

        var locals = cards
            .Select((card, index) => ReadCard(card, index))
            .Where(card => card is not null)
            .Cast<LocalPokemonCard>()
            .ToArray();

        var available = remoteParty
            .Select((pokemon, index) => new Candidate(pokemon, index))
            .ToList();

        var localLocations = locals
            .Select(local => NormalizeLocation(local.Location))
            .Where(location => location.Length > 0)
            .ToHashSet(StringComparer.Ordinal);
        var hasLocationOverlap = available.Any(candidate =>
            localLocations.Contains(NormalizeLocation(candidate.Pokemon.Location)));

        foreach (var local in locals)
        {
            var view = GetOrCreateView(local);
            if (view is null)
            {
                continue;
            }

            Candidate? match = null;
            var location = NormalizeLocation(local.Location);
            if (location.Length > 0)
            {
                match = available.FirstOrDefault(candidate =>
                    NormalizeLocation(candidate.Pokemon.Location) == location);
            }

            match ??= available.FirstOrDefault(candidate =>
                IsSamePokemon(local, candidate.Pokemon));

            if (match is null &&
                !hasLocationOverlap &&
                local.Index < remoteParty.Count)
            {
                match = available.FirstOrDefault(candidate =>
                    candidate.Index == local.Index);
            }

            if (match is null)
            {
                ShowUnlinked(view);
                continue;
            }

            available.Remove(match);
            ShowLinked(
                view,
                match.Pokemon,
                local.CurrentHp == 0 || match.Pokemon.CurrentHp == 0);
        }
    }

    private static IReadOnlyList<Button> FindTeamCards(Window window)
    {
        return window
            .GetVisualDescendants()
            .OfType<Button>()
            .Where(IsTeamPokemonCard)
            .OrderBy(card => Grid.GetRow(card))
            .ThenBy(card => Grid.GetColumn(card))
            .Take(6)
            .ToArray();
    }

    private static bool IsTeamPokemonCard(Button card)
    {
        var texts = card.GetVisualDescendants().OfType<TextBlock>().ToArray();
        return texts.Any(text =>
                   text.Text?.StartsWith("📍", StringComparison.Ordinal) == true) &&
               texts.Any(text =>
                   text.Text?.StartsWith("🔗", StringComparison.Ordinal) == true);
    }

    private static LocalPokemonCard? ReadCard(Button card, int index)
    {
        var texts = card.GetVisualDescendants().OfType<TextBlock>().ToArray();
        var locationText = texts.FirstOrDefault(text =>
            text.Text?.StartsWith("📍", StringComparison.Ordinal) == true);
        var linkText = texts.FirstOrDefault(text =>
            text.Text?.StartsWith("🔗", StringComparison.Ordinal) == true);

        if (locationText?.Text is not { } rawLocation || linkText is null)
        {
            return null;
        }

        var name = texts
            .Select(text => text.Text)
            .FirstOrDefault(IsPokemonName)
            ?? "Unbekannt";
        var level = ParseLevel(texts);
        var currentHp = ParseCurrentHp(texts);

        return new LocalPokemonCard(
            card,
            linkText,
            index,
            rawLocation[2..].Trim(),
            name,
            level,
            currentHp);
    }

    private static PartnerView? GetOrCreateView(LocalPokemonCard local)
    {
        if (Views.TryGetValue(local.Card, out var existing))
        {
            return existing;
        }

        if (local.Card.Content is not Control originalContent)
        {
            return null;
        }

        local.LinkText.IsVisible = false;

        var image = new Image
        {
            Width = 36,
            Height = 36,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var label = new TextBlock
        {
            Text = "SOULLINK",
            FontSize = 7,
            FontWeight = FontWeight.Bold,
            Foreground = Brush("#FBBF24"),
            TextAlignment = TextAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var name = new TextBlock
        {
            FontSize = 8,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#F8FAFC"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var partnerStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { label, image, name }
        };
        var partnerBorder = new Border
        {
            Background = Brush("#2A2111"),
            BorderBrush = Brush("#D97706"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(3, 2),
            MinWidth = 0,
            Child = partnerStack
        };
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("3*,1*"),
            ColumnSpacing = 5,
            MinWidth = 0
        };
        layout.Children.Add(originalContent);
        Grid.SetColumn(partnerBorder, 1);
        layout.Children.Add(partnerBorder);
        local.Card.Content = layout;

        var view = new PartnerView(partnerBorder, label, image, name);
        Views[local.Card] = view;
        ShowUnlinked(view);
        return view;
    }

    private static void ShowUnlinked(PartnerView view)
    {
        if (view.Signature == "unlinked")
        {
            return;
        }

        view.Signature = "unlinked";
        view.SpeciesId = 0;
        view.Image.Source = null;
        view.Label.Text = "SOULLINK";
        view.Label.Foreground = Brush("#FBBF24");
        view.Name.Text = "Noch nicht\nverknüpft";
        view.Border.Background = Brush("#2A2111");
        view.Border.BorderBrush = Brush("#D97706");
    }

    private static void ShowLinked(
        PartnerView view,
        NetworkPokemonSnapshot pokemon,
        bool fainted)
    {
        var signature =
            $"{pokemon.SpeciesId}:{pokemon.DisplayName}:{pokemon.CurrentHp}:{fainted}";
        if (view.Signature == signature)
        {
            return;
        }

        view.Signature = signature;
        view.Label.Text = fainted ? "KAMPFUNFÄHIG" : "VERKNÜPFT MIT";
        view.Label.Foreground = Brush(fainted ? "#FCA5A5" : "#86EFAC");
        view.Name.Text = pokemon.DisplayName;
        view.Border.Background = Brush(fainted ? "#301717" : "#10251F");
        view.Border.BorderBrush = Brush(fainted ? "#EF4444" : "#22C55E");

        if (view.SpeciesId != pokemon.SpeciesId)
        {
            view.SpeciesId = pokemon.SpeciesId;
            view.Image.Source = null;
            _ = LoadSpriteAsync(view, pokemon.SpeciesId);
        }
    }

    private static async Task LoadSpriteAsync(PartnerView view, int speciesId)
    {
        var visual = await VisualService.GetAsync(speciesId, false);
        if (view.SpeciesId == speciesId && visual.Sprite is not null)
        {
            view.Image.Source = visual.Sprite;
        }
    }

    private static bool IsPokemonName(string? value)
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

    private static int ParseLevel(IEnumerable<TextBlock> texts)
    {
        var value = texts.Select(text => text.Text).FirstOrDefault(text =>
            text?.StartsWith("Level ", StringComparison.Ordinal) == true);
        return value is not null && int.TryParse(value[6..], out var level)
            ? level
            : 0;
    }

    private static int ParseCurrentHp(IEnumerable<TextBlock> texts)
    {
        var value = texts.Select(text => text.Text).FirstOrDefault(text =>
            !string.IsNullOrWhiteSpace(text) &&
            text.EndsWith(" KP", StringComparison.Ordinal) &&
            text.Contains('/'));
        if (value is null)
        {
            return -1;
        }

        var current = value.Replace(" KP", string.Empty)
            .Split('/', StringSplitOptions.TrimEntries)
            .FirstOrDefault();
        return int.TryParse(current, out var hp) ? hp : -1;
    }

    private static bool IsSamePokemon(
        LocalPokemonCard local,
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
               (displayName.Length > 0 &&
                localName.Contains(displayName, StringComparison.Ordinal)) ||
               (speciesName.Length > 0 &&
                localName.Contains(speciesName, StringComparison.Ordinal));
    }

    private static string NormalizeLocation(string value)
    {
        var normalized = NormalizeName(value);
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

    private static SolidColorBrush Brush(string value) =>
        new(Color.Parse(value));

    private sealed record Candidate(
        NetworkPokemonSnapshot Pokemon,
        int Index);

    private sealed record LocalPokemonCard(
        Button Card,
        TextBlock LinkText,
        int Index,
        string Location,
        string Name,
        int Level,
        int CurrentHp);

    private sealed class PartnerView(
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
}
