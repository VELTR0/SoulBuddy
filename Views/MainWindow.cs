using System.ComponentModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using SoulBuddy.Models;
using SoulBuddy.Services;
using SoulBuddy.ViewModels;

namespace SoulBuddy.Views;

public sealed class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private readonly PokemonVisualService _visualService = new();
    private readonly SessionContext? _sessionContext;
    private readonly Grid _partyPanel = new()
    {
        RowDefinitions = new RowDefinitions("*,*,*,*,*,*"),
        RowSpacing = 5
    };
    private readonly StackPanel _storedPanel = new()
    {
        Spacing = 7,
        Margin = new Thickness(0, 0, 8, 0)
    };
    private Border? _gameStatusBadge;
    private Border? _gameStatusDot;
    private TextBlock? _gameStatusText;
    private Border? _serverStatusBadge;
    private Border? _serverStatusDot;
    private TextBlock? _serverStatusText;
    private TextBlock? _partnerActivePokemonText;
    private bool _compact;

    public MainWindow(SessionContext? sessionContext = null)
    {
        _sessionContext = sessionContext;

        Title = sessionContext is null
            ? "SoulBuddy"
            : $"SoulBuddy · {sessionContext.Session.Name} · {sessionContext.LocalPlayer.DisplayName}";
        Width = 1380;
        Height = 1400;
        MinWidth = 550;
        MinHeight = 450;
        Background = Brush("#0B1220");
        DataContext = _viewModel;

        _viewModel.Party.CollectionChanged += (_, _) => RenderParty();
        _viewModel.StoredPokemon.CollectionChanged += (_, _) => RenderStoredPokemon();
        _viewModel.PropertyChanged += OnViewModelPropertyChanged;

        Content = BuildLayout();
        Opened += OnOpened;
        Closing += OnClosing;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private Control BuildLayout()
    {
        var root = new Grid { RowDefinitions = new RowDefinitions("Auto,Auto,*") };
        root.Children.Add(BuildHeader());

        var sessionPanel = BuildSessionPanel();
        Grid.SetRow(sessionPanel, 1);
        root.Children.Add(sessionPanel);

        var content = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("2.15*,0.62*,1.1*"),
            ColumnSpacing = 12,
            Margin = new Thickness(16, 12, 16, 14)
        };
        Grid.SetRow(content, 2);
        content.Children.Add(Card(BuildPartySection()));

        var stored = Card(BuildScrollableSection("Encounters", "PokemonCountText", _storedPanel));
        Grid.SetColumn(stored, 1);
        content.Children.Add(stored);

        var right = Card(BuildRightColumn());
        Grid.SetColumn(right, 2);
        content.Children.Add(right);
        root.Children.Add(content);

        return root;
    }

    private Control BuildHeader()
    {
        var border = new Border
        {
            Background = Brush("#101A2E"),
            BorderBrush = Brush("#263650"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(16, 9)
        };
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto") };
        grid.Children.Add(Text("SoulBuddy", 23, FontWeight.Bold, "#F8FAFC"));

        _gameStatusBadge = BuildStatusBadge(
            "LocalPlayerStatus",
            IsEmulatorConnected(),
            out _gameStatusDot,
            out _gameStatusText);
        _serverStatusBadge = BuildStatusBadge(
            "ServerSyncStatus",
            IsServerConnected(),
            out _serverStatusDot,
            out _serverStatusText);

        var headerActions = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { _gameStatusBadge, _serverStatusBadge, BuildLanguageButton() }
        };
        Grid.SetColumn(headerActions, 1);
        grid.Children.Add(headerActions);

        border.Child = grid;
        return border;
    }

    private static Border BuildStatusBadge(
        string binding,
        bool connected,
        out Border dot,
        out TextBlock status)
    {
        status = BoundText(
            binding,
            10,
            FontWeight.Bold,
            connected ? "#A7F3D0" : "#FDE68A");
        status.VerticalAlignment = VerticalAlignment.Center;

        dot = new Border
        {
            Width = 7,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            Background = Brush(connected ? "#4ADE80" : "#FBBF24"),
            VerticalAlignment = VerticalAlignment.Center
        };

        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7,
            VerticalAlignment = VerticalAlignment.Center,
            Children = { dot, status }
        };

        return new Border
        {
            Background = Brush(connected ? "#123128" : "#2A2111"),
            BorderBrush = Brush(connected ? "#2F765E" : "#D97706"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = content
        };
    }

    private static void ApplyStatusBadgeState(
        Border? badge,
        Border? dot,
        TextBlock? status,
        bool connected)
    {
        if (badge is null || dot is null || status is null)
            return;

        badge.Background = Brush(connected ? "#123128" : "#2A2111");
        badge.BorderBrush = Brush(connected ? "#2F765E" : "#D97706");
        dot.Background = Brush(connected ? "#4ADE80" : "#FBBF24");
        status.Foreground = Brush(connected ? "#A7F3D0" : "#FDE68A");
    }

    private bool IsEmulatorConnected() =>
        string.Equals(
            _viewModel.LocalPlayerStatus,
            "Spiel verbunden",
            StringComparison.Ordinal);

    private bool IsServerConnected() =>
        string.Equals(
            _viewModel.ServerSyncStatus,
            "Server verbunden",
            StringComparison.Ordinal);

    private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
    {
        if (eventArgs.PropertyName == nameof(MainWindowViewModel.LocalPlayerStatus))
        {
            ApplyStatusBadgeState(
                _gameStatusBadge,
                _gameStatusDot,
                _gameStatusText,
                IsEmulatorConnected());
        }
        else if (eventArgs.PropertyName == nameof(MainWindowViewModel.ServerSyncStatus))
        {
            ApplyStatusBadgeState(
                _serverStatusBadge,
                _serverStatusDot,
                _serverStatusText,
                IsServerConnected());
        }

        if (eventArgs.PropertyName == nameof(MainWindowViewModel.LocalActivePokemonText))
            UpdatePartnerActivePokemonDisplay();
    }

    private static Button BuildLanguageButton()
    {
        var languageMenu = new MenuFlyout();

        languageMenu.Items.Add(new MenuItem { Header = "🇩🇪  Deutsch" });
        languageMenu.Items.Add(new MenuItem { Header = "🇬🇧  English" });
        languageMenu.Items.Add(new MenuItem { Header = "🇫🇷  Français" });
        languageMenu.Items.Add(new MenuItem { Header = "🇪🇸  Español" });
        languageMenu.Items.Add(new MenuItem { Header = "🇮🇹  Italiano" });
        languageMenu.Items.Add(new MenuItem { Header = "🇯🇵  日本語" });

        return new Button
        {
            Content = "🇩🇪",
            Flyout = languageMenu,
            Width = 42,
            Height = 32,
            Padding = new Thickness(6, 2),
            FontSize = 17,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
            Background = Brush("#17243A"),
            BorderBrush = Brush("#344763"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9)
        };
    }

    private Control BuildSessionPanel()
    {
        var session = _sessionContext?.Session;
        var sessionLinkValue = _sessionContext?.SoullockeLink ?? string.Empty;

        var sessionStack = new StackPanel
        {
            Spacing = 5,
            Margin = new Thickness(4, 2)
        };
        sessionStack.Children.Add(Text("AKTIVE SESSION", 9, FontWeight.Bold, "#93C5FD"));
        sessionStack.Children.Add(Text(session?.Name ?? "SoulLocke", 13, FontWeight.Bold, "#F8FAFC"));

        var sessionLinkRow = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 7
        };
        var sessionLinkLabel = Text("Session Link:", 10, FontWeight.Normal, "#CBD5E1");
        sessionLinkLabel.VerticalAlignment = VerticalAlignment.Center;
        sessionLinkRow.Children.Add(sessionLinkLabel);

        var sessionLinkBox = new TextBox
        {
            Text = string.IsNullOrWhiteSpace(sessionLinkValue) ? "–" : sessionLinkValue,
            IsReadOnly = true,
            FontSize = 10,
            Foreground = Brush("#E2E8F0"),
            Background = Brush("#0F1829"),
            BorderBrush = Brush("#344763"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6),
            Padding = new Thickness(7, 4),
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        Grid.SetColumn(sessionLinkBox, 1);
        sessionLinkRow.Children.Add(sessionLinkBox);

        var copyButton = new Button
        {
            Content = "Kopieren",
            IsEnabled = !string.IsNullOrWhiteSpace(sessionLinkValue),
            Padding = new Thickness(9, 4),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Background = Brush("#17243A"),
            Foreground = Brush("#E2E8F0"),
            BorderBrush = Brush("#344763"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        copyButton.Click += async (_, _) =>
        {
            if (string.IsNullOrWhiteSpace(sessionLinkValue))
                return;

            var clipboard = TopLevel.GetTopLevel(this)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(sessionLinkValue);
        };
        Grid.SetColumn(copyButton, 2);
        sessionLinkRow.Children.Add(copyButton);
        sessionStack.Children.Add(sessionLinkRow);

        var sessionCard = new Border
        {
            Background = Brush("#151F33"),
            BorderBrush = Brush("#2B3C58"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 10),
            Child = sessionStack
        };

        var partnerStack = new StackPanel
        {
            Spacing = 4,
            Margin = new Thickness(4, 2)
        };
        partnerStack.Children.Add(Text("MITTSPIELER", 9, FontWeight.Bold, "#93C5FD"));
        var partnerStatus = BoundText("PartnerStatus", 10, FontWeight.Normal, "#A7F3D0");
        partnerStatus.TextWrapping = TextWrapping.Wrap;
        partnerStack.Children.Add(partnerStatus);
        _partnerActivePokemonText = Text("Aktiv: wird ermittelt …", 10, FontWeight.Normal, "#CBD5E1");
        _partnerActivePokemonText.TextWrapping = TextWrapping.Wrap;
        partnerStack.Children.Add(_partnerActivePokemonText);

        var playersCard = new Border
        {
            Background = Brush("#151F33"),
            BorderBrush = Brush("#2B3C58"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(12, 10),
            Child = partnerStack
        };

        var cards = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("0.85*,1.65*"),
            ColumnSpacing = 12,
            Margin = new Thickness(16, 9, 16, 0)
        };
        cards.Children.Add(sessionCard);
        Grid.SetColumn(playersCard, 1);
        cards.Children.Add(playersCard);
        return cards;
    }

    private Control BuildPartySection()
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(SectionHeader("Aktuelles Team", "PartyCountText"));
        Grid.SetRow(_partyPanel, 1);
        grid.Children.Add(_partyPanel);
        return grid;
    }

    private Control BuildScrollableSection(string title, string countBinding, Control content)
    {
        var grid = new Grid { RowDefinitions = new RowDefinitions("Auto,*") };
        grid.Children.Add(SectionHeader(title, countBinding));
        var scroll = new ScrollViewer
        {
            Content = content,
            Margin = new Thickness(0, 0, -8, 0),
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        return grid;
    }

    private Control BuildRightColumn()
    {
        var tabs = new TabControl { SelectedIndex = 1 };

        var livePanel = new StackPanel { Spacing = 8, Margin = new Thickness(2) };
        var liveTitle = BoundText("LiveEncounterTitle", 12, FontWeight.Bold, "#93C5FD");
        livePanel.Children.Add(liveTitle);
        var liveText = BoundText("LiveEncounterText", 12, FontWeight.Normal, "#E2E8F0");
        liveText.TextWrapping = TextWrapping.Wrap;
        liveText.LineHeight = 18;
        livePanel.Children.Add(liveText);

        var detailsPanel = new StackPanel { Spacing = 8, Margin = new Thickness(2) };
        var detailsTitle = BoundText("DetailsTitle", 17, FontWeight.Bold, "#F8FAFC");
        detailsTitle.TextWrapping = TextWrapping.Wrap;
        detailsPanel.Children.Add(detailsTitle);
        var details = BoundText("DetailsText", 11, FontWeight.Normal, "#CBD5E1");
        details.TextWrapping = TextWrapping.Wrap;
        details.LineHeight = 17;
        detailsPanel.Children.Add(details);

        tabs.Items.Add(new TabItem
        {
            Header = "Live",
            Content = new ScrollViewer
            {
                Content = livePanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        });
        tabs.Items.Add(new TabItem
        {
            Header = "Details",
            Content = new ScrollViewer
            {
                Content = detailsPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto
            }
        });
        return tabs;
    }

    private Control SectionHeader(string title, string countBinding)
    {
        var grid = new Grid { ColumnDefinitions = new ColumnDefinitions("*,Auto"), Margin = new Thickness(0, 0, 0, 7) };
        grid.Children.Add(Text(title, 15, FontWeight.Bold, "#F8FAFC"));
        var count = BoundText(countBinding, 10, FontWeight.Medium, "#93C5FD");
        Grid.SetColumn(count, 1);
        grid.Children.Add(count);
        return grid;
    }

    private void RenderParty()
    {
        _partyPanel.Children.Clear();
        for (var slot = 0; slot < 6; slot++)
        {
            Control card = slot < _viewModel.Party.Count
                ? PokemonCard(_viewModel.Party[slot], true)
                : EmptyPartySlot();
            Grid.SetRow(card, slot);
            _partyPanel.Children.Add(card);
        }

        UpdatePartnerActivePokemonDisplay();
    }

    private void UpdatePartnerActivePokemonDisplay()
    {
        if (_partnerActivePokemonText is null)
            return;

        const string prefix = "Aktiv: ";
        var activeText = _viewModel.LocalActivePokemonText;
        if (!activeText.StartsWith(prefix, StringComparison.Ordinal))
        {
            _partnerActivePokemonText.Text = "Aktiv: wird ermittelt …";
            return;
        }

        var separatorIndex = activeText.IndexOf(" ·", prefix.Length, StringComparison.Ordinal);
        var localActiveName = separatorIndex > prefix.Length
            ? activeText[prefix.Length..separatorIndex]
            : activeText[prefix.Length..];

        var activeCard = _viewModel.Party.FirstOrDefault(pokemon =>
            string.Equals(pokemon.DisplayName, localActiveName, StringComparison.Ordinal));
        var partnerLink = activeCard?.SoullockePartnerLink;

        _partnerActivePokemonText.Text = partnerLink is null
            ? "Aktiv: nicht verknüpft"
            : $"Aktiv: {activeCard!.LinkedNameLine}";
    }

    private void RenderStoredPokemon()
    {
        _storedPanel.Children.Clear();
        if (_viewModel.StoredPokemon.Count == 0)
        {
            _storedPanel.Children.Add(EmptyState("Noch keine Pokémon lokal gespeichert."));
            return;
        }

        foreach (var pokemon in _viewModel.StoredPokemon)
            _storedPanel.Children.Add(PokemonCard(pokemon, false));
    }

    private Control PokemonCard(PokemonCardViewModel pokemon, bool party)
    {
        var small = party && _compact;
        var failedEncounter = !party && IsFailedEncounter(pokemon);
        var healthyEncounter = !party && IsHealthyEncounter(pokemon);
        var cardBackground = failedEncounter ? "#301717" : healthyEncounter ? "#10251F" : "#0F1829";
        var cardBorder = failedEncounter ? "#EF4444" : healthyEncounter ? "#22C55E" : "#344763";
        var spriteBackground = failedEncounter ? "#3A1A1A" : healthyEncounter ? "#143026" : "#18243A";
        var spriteBorder = failedEncounter ? "#7F1D1D" : healthyEncounter ? "#2F765E" : "#334866";
        var subtitleColor = failedEncounter ? "#FCA5A5" : healthyEncounter ? "#86EFAC" : "#94A3B8";

        var sprite = new Image { Width = small ? 34 : 52, Height = small ? 34 : 52, Stretch = Stretch.Uniform };
        var spriteFrame = new Border
        {
            Width = small ? 40 : 58,
            Height = small ? 40 : 58,
            Background = Brush(spriteBackground),
            BorderBrush = Brush(spriteBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = sprite
        };

        var text = new StackPanel { Spacing = small ? 1 : 3 };
        var name = Text(pokemon.NameLine, small ? 10 : 13, FontWeight.SemiBold, "#F8FAFC");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(name);
        var location = Text($"📍 {pokemon.Subtitle}", small ? 8 : 10, FontWeight.Normal, subtitleColor);
        location.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(location);

        if (party)
        {
            var hpGrid = new Grid
            {
                RowDefinitions = new RowDefinitions("Auto,Auto"),
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 5,
                RowSpacing = 2,
                MinWidth = 0
            };
            hpGrid.Children.Add(Text(pokemon.LevelText, small ? 8 : 9, FontWeight.Medium, "#93C5FD"));
            var hp = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = pokemon.HpPercentage,
                MinWidth = 0,
                Height = 5,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                VerticalAlignment = VerticalAlignment.Center,
                Background = Brush("#263650"),
                Foreground = Brush(HpColor(pokemon.HpPercentage))
            };
            Grid.SetRow(hp, 1);
            hpGrid.Children.Add(hp);
            var hpText = Text(pokemon.HpText, small ? 8 : 9, FontWeight.Medium, "#CBD5E1");
            Grid.SetRow(hpText, 1);
            Grid.SetColumn(hpText, 1);
            hpGrid.Children.Add(hpText);
            text.Children.Add(hpGrid);
        }
        else
        {
            text.Children.Add(Text(pokemon.LevelText, 9, FontWeight.Medium, healthyEncounter ? "#86EFAC" : failedEncounter ? "#FCA5A5" : "#93C5FD"));
        }

        var ownLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("Auto,*"), ColumnSpacing = small ? 6 : 9, MinWidth = 0 };
        ownLayout.Children.Add(spriteFrame);
        Grid.SetColumn(text, 1);
        ownLayout.Children.Add(text);

        Control content = ownLayout;
        if (party)
        {
            var completeLayout = new Grid { ColumnDefinitions = new ColumnDefinitions("3*,1*"), ColumnSpacing = small ? 4 : 7, MinWidth = 0 };
            completeLayout.Children.Add(ownLayout);
            var partner = BuildSoulLinkPanel(pokemon, small);
            Grid.SetColumn(partner, 1);
            completeLayout.Children.Add(partner);
            content = completeLayout;
        }

        var button = new Button
        {
            Content = content,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = party ? VerticalAlignment.Stretch : VerticalAlignment.Center,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(small ? 3 : 7),
            Background = Brush(cardBackground),
            BorderBrush = Brush(cardBorder),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        button.Click += (_, _) => _viewModel.SelectPokemon(pokemon);
        _ = LoadSpriteAsync(pokemon.SpeciesId, pokemon.IsShiny, sprite);
        return button;
    }

    private static bool IsFailedEncounter(PokemonCardViewModel pokemon) =>
        pokemon.Subtitle.EndsWith(" · Nicht gefangen", StringComparison.OrdinalIgnoreCase) ||
        pokemon.Subtitle.EndsWith(" · Besiegt", StringComparison.OrdinalIgnoreCase) ||
        pokemon.Subtitle.EndsWith(" · Bro-Failed", StringComparison.OrdinalIgnoreCase);

    private static bool IsHealthyEncounter(PokemonCardViewModel pokemon) =>
        pokemon.Subtitle.EndsWith(" · Lebendig", StringComparison.OrdinalIgnoreCase) ||
        pokemon.Subtitle.EndsWith(" · Box", StringComparison.OrdinalIgnoreCase);

    private Control BuildSoulLinkPanel(PokemonCardViewModel pokemon, bool small)
    {
        var linked = pokemon.IsSoulLinked;
        var fainted = pokemon.LinkedIsFainted;
        var image = new Image
        {
            Width = small ? 28 : 40,
            Height = small ? 28 : 40,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var label = Text(
            linked ? fainted ? "KAMPFUNFÄHIG" : "VERKNÜPFT MIT" : "SOULLINK",
            small ? 6 : 7,
            FontWeight.Bold,
            linked ? fainted ? "#FCA5A5" : "#86EFAC" : "#FBBF24");
        label.TextAlignment = TextAlignment.Center;

        var partnerName = Text(
            linked ? pokemon.LinkedNameLine : "Noch nicht\nverknüpft",
            small ? 7 : 8,
            FontWeight.SemiBold,
            linked ? "#F8FAFC" : "#FDE68A");
        partnerName.TextAlignment = TextAlignment.Center;
        partnerName.TextWrapping = TextWrapping.Wrap;
        partnerName.MaxLines = 2;

        var stack = new StackPanel
        {
            Spacing = 1,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Children = { label, image, partnerName }
        };
        var border = new Border
        {
            Background = Brush(linked ? fainted ? "#301717" : "#10251F" : "#2A2111"),
            BorderBrush = Brush(linked ? fainted ? "#EF4444" : "#22C55E" : "#D97706"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7),
            Padding = new Thickness(3, 2),
            MinWidth = 0,
            Child = stack
        };

        if (linked && pokemon.LinkedSpeciesId > 0)
            _ = LoadSpriteAsync(pokemon.LinkedSpeciesId, false, image);

        return border;
    }

    private static Control EmptyPartySlot() => new Border
    {
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        Background = Brush("#0F1829"),
        BorderBrush = Brush("#263650"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(8)
    };

    private static Control EmptyState(string value) => new Border
    {
        Background = Brush("#0F1829"),
        CornerRadius = new CornerRadius(8),
        Padding = new Thickness(10),
        Child = Text(value, 11, FontWeight.Normal, "#94A3B8")
    };

    private async Task LoadSpriteAsync(int speciesId, bool isShiny, Image image)
    {
        var visual = await _visualService.GetAsync(speciesId, isShiny);
        if (visual.Sprite is not null)
            image.Source = visual.Sprite;
    }

    private void ApplyResponsiveLayout()
    {
        var compact = Bounds.Width < 1050 || Bounds.Height < 720;
        if (_compact == compact)
            return;

        _compact = compact;
        _partyPanel.RowSpacing = compact ? 2 : 5;
        RenderParty();
    }

    private static Border Card(Control child) => new()
    {
        Background = Brush("#151F33"),
        BorderBrush = Brush("#2B3C58"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(11),
        Padding = new Thickness(10),
        Child = child
    };

    private static TextBlock BoundText(string property, double size, FontWeight weight, string color)
    {
        var text = Text(string.Empty, size, weight, color);
        text.Bind(TextBlock.TextProperty, new Binding(property));
        return text;
    }

    private static TextBlock Text(string value, double size, FontWeight weight, string color) => new()
    {
        Text = value,
        FontSize = size,
        FontWeight = weight,
        Foreground = Brush(color)
    };

    private static string HpColor(double value) => value <= 20 ? "#F87171" : value <= 50 ? "#FBBF24" : "#4ADE80";
    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        ApplyResponsiveLayout();
        RenderParty();
        RenderStoredPokemon();
        await _viewModel.InitializeAsync();
        RenderParty();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        _viewModel.PropertyChanged -= OnViewModelPropertyChanged;
        await _viewModel.DisposeAsync();
        _visualService.Dispose();
    }
}
