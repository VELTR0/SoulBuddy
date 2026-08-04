using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoulBuddy.Models;
using SoulBuddy.ViewModels;
using SoulBuddy.Views;

namespace SoulBuddy.Services;

internal static class DirectSoulLinkUiUpdater
{
    private static readonly FieldInfo? PartyPanelField = typeof(MainWindow).GetField(
        "_partyPanel",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? ViewModelField = typeof(MainWindow).GetField(
        "_viewModel",
        BindingFlags.Instance | BindingFlags.NonPublic);

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
                Interval = TimeSpan.FromMilliseconds(350)
            };
            _timer.Tick += (_, _) => UpdateOpenWindows();
            _timer.Start();
            UpdateOpenWindows();
        });
    }

    private static void UpdateOpenWindows()
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

        var windows = desktop.Windows.OfType<MainWindow>().ToArray();
        foreach (var window in windows)
        {
            UpdateConnectionHeadline(window, connected);
            UpdateTeamCards(window, remoteParty);
        }

        RemoveDetachedViews(windows);
    }

    private static void UpdateConnectionHeadline(MainWindow window, bool connected)
    {
        foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
        {
            if (text.Text is "🟡 Nicht verbunden" or "🟡 Lokal eingetragen")
            {
                text.IsVisible = !connected;
                text.Height = connected ? 0 : double.NaN;
                if (connected)
                {
                    text.Margin = new Thickness(0);
                }
            }
        }
    }

    private static void UpdateTeamCards(
        MainWindow window,
        IReadOnlyList<NetworkPokemonSnapshot> remoteParty)
    {
        if (PartyPanelField?.GetValue(window) is not Grid partyPanel ||
            ViewModelField?.GetValue(window) is not MainWindowViewModel viewModel)
        {
            return;
        }

        var cards = partyPanel.Children
            .OfType<Button>()
            .OrderBy(card => Grid.GetRow(card))
            .ThenBy(card => Grid.GetColumn(card))
            .ToArray();

        var localParty = viewModel.Party.ToArray();
        var count = Math.Min(cards.Length, localParty.Length);
        var available = remoteParty
            .Select((pokemon, index) => new Candidate(pokemon, index))
            .ToList();

        var anyLocationOverlap = localParty.Any(local =>
            available.Any(remote =>
                NormalizeLocation(local.Subtitle) ==
                NormalizeLocation(remote.Pokemon.Location)));

        for (var index = 0; index < count; index++)
        {
            var card = cards[index];
            var local = localParty[index];
            var view = GetOrCreateView(card);
            if (view is null)
            {
                continue;
            }

            var localLocation = NormalizeLocation(local.Subtitle);
            var match = available.FirstOrDefault(candidate =>
                localLocation.Length > 0 &&
                NormalizeLocation(candidate.Pokemon.Location) == localLocation);

            match ??= available.FirstOrDefault(candidate =>
                IsSamePokemon(local, candidate.Pokemon));

            if (match is null && !anyLocationOverlap)
            {
                match = available.FirstOrDefault(candidate => candidate.Index == index);
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

    private static PartnerView? GetOrCreateView(Button card)
    {
        if (Views.TryGetValue(card, out var existing))
        {
            return existing;
        }

        if (card.Content is not Control originalContent)
        {
            return null;
        }

        var oldLink = originalContent
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text =>
                text.Text?.StartsWith("🔗", StringComparison.Ordinal) == true);
        if (oldLink is not null)
        {
            oldLink.IsVisible = false;
            oldLink.Height = 0;
            oldLink.Margin = new Thickness(0);
        }

        var image = new Image
        {
            Width = 38,
            Height = 38,
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
        var partnerName = new TextBlock
        {
            Text = "Noch nicht\nverknüpft",
            FontSize = 8,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#FDE68A"),
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            MaxLines = 2,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        var partnerStack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { label, image, partnerName }
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
        card.Content = layout;

        var view = new PartnerView(partnerBorder, label, image, partnerName);
        Views[card] = view;
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
        view.Name.Foreground = Brush("#FDE68A");
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
        view.Name.Text = string.IsNullOrWhiteSpace(pokemon.DisplayName)
            ? pokemon.SpeciesName
            : pokemon.DisplayName;
        view.Name.Foreground = Brush("#F8FAFC");
        view.Border.Background = Brush(fainted ? "#301717" : "#10251F");
        view.Border.BorderBrush = Brush(fainted ? "#EF4444" : "#22C55E");

        if (view.SpeciesId != pokemon.SpeciesId)
        {
            view.SpeciesId = pokemon.SpeciesId;
            view.Image.Source = null;
            _ = LoadSpriteAsync(view, pokemon.SpeciesId);
        }
    }

    private static bool IsSamePokemon(
        PokemonCardViewModel local,
        NetworkPokemonSnapshot remote)
    {
        if (local.Level > 0 && remote.Level != local.Level)
        {
            return false;
        }

        var localDisplay = Normalize(local.DisplayName);
        var localSpecies = Normalize(local.Species);
        var remoteDisplay = Normalize(remote.DisplayName);
        var remoteSpecies = Normalize(remote.SpeciesName);

        return localDisplay == remoteDisplay ||
               localDisplay == remoteSpecies ||
               localSpecies == remoteSpecies ||
               localSpecies == remoteDisplay;
    }

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

    private static async Task LoadSpriteAsync(PartnerView view, int speciesId)
    {
        var visual = await VisualService.GetAsync(speciesId, false);
        if (view.SpeciesId == speciesId && visual.Sprite is not null)
        {
            view.Image.Source = visual.Sprite;
        }
    }

    private static void RemoveDetachedViews(IEnumerable<MainWindow> windows)
    {
        var liveCards = windows
            .SelectMany(window =>
                PartyPanelField?.GetValue(window) is Grid panel
                    ? panel.Children.OfType<Button>()
                    : [])
            .ToHashSet();

        foreach (var card in Views.Keys.Where(card => !liveCards.Contains(card)).ToArray())
        {
            Views.Remove(card);
        }
    }

    private static SolidColorBrush Brush(string value) =>
        new(Color.Parse(value));

    private sealed record Candidate(NetworkPokemonSnapshot Pokemon, int Index);

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
