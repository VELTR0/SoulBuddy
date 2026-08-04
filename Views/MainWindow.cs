using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SoulBuddy.Models;
using SoulBuddy.Services;
using SoulBuddy.ViewModels;

namespace SoulBuddy.Views;

public sealed class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel = new();
    private readonly PokemonVisualService _visualService = new();
    private readonly SoulBuddyNetworkService _networkService = new();
    private readonly SessionContext? _sessionContext;
    private readonly Grid _partyPanel = new()
    {
        RowDefinitions = new RowDefinitions("*,*,*,*,*,*"),
        RowSpacing = 5
    };
    private readonly StackPanel _storedPanel = new() { Spacing = 7 };
    private TextBlock? _networkStatusText;
    private bool _compact;

    public MainWindow(SessionContext? sessionContext = null)
    {
        _sessionContext = sessionContext;
        Title = sessionContext is null
            ? "SoulBuddy"
            : $"SoulBuddy · {sessionContext.Session.Name} · {sessionContext.LocalPlayer.DisplayName}";
        Width = 1380;
        Height = 900;
        MinWidth = 620;
        MinHeight = 500;
        Background = Brush("#0B1220");
        DataContext = _viewModel;

        _viewModel.Party.CollectionChanged += (_, _) => RenderParty();
        _viewModel.StoredPokemon.CollectionChanged += (_, _) => RenderStoredPokemon();
        _networkService.StatusChanged += OnNetworkStatusChanged;
        _networkService.PlayerSnapshotReceived += OnPlayerSnapshotReceived;

        Content = BuildLayout();
        Opened += OnOpened;
        Closing += OnClosing;
        SizeChanged += (_, _) => ApplyResponsiveLayout();
    }

    private Control BuildLayout()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*,Auto")
        };
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

        var stored = Card(BuildScrollableSection(
            "Gespeicherte Pokémon",
            "PokemonCountText",
            _storedPanel));
        Grid.SetColumn(stored, 1);
        content.Children.Add(stored);

        var right = Card(BuildRightColumn());
        Grid.SetColumn(right, 2);
        content.Children.Add(right);
        root.Children.Add(content);

        var footer = new Border
        {
            Background = Brush("#101A2E"),
            BorderBrush = Brush("#263650"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(14, 7)
        };
        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        var status = BoundText("StatusText", 11, FontWeight.Medium, "#CBD5E1");
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        footerGrid.Children.Add(status);
        var connection = BoundText("ConnectionText", 11, FontWeight.SemiBold, "#7DD3FC");
        Grid.SetColumn(connection, 1);
        footerGrid.Children.Add(connection);
        footer.Child = footerGrid;
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);
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
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        var title = new StackPanel { Spacing = 1 };
        title.Children.Add(Text("SoulBuddy", 23, FontWeight.Bold, "#F8FAFC"));
        title.Children.Add(Text(
            "Eigenständiger SoulLink- und Nuzlocke-Begleiter",
            11,
            FontWeight.Normal,
            "#94A3B8"));
        grid.Children.Add(title);
        var badge = new Border
        {
            Background = Brush("#172554"),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(10, 5),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text("PHASE 3C", 10, FontWeight.Bold, "#93C5FD")
        };
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);
        border.Child = grid;
        return border;
    }

    private Control BuildSessionPanel()
    {
        var session = _sessionContext?.Session;
        var local = _sessionContext?.LocalPlayer;
        var partner = session?.Players.FirstOrDefault(player => player.Id != local?.Id);
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("0.9*,1.25*,1.25*"),
            ColumnSpacing = 9,
            Margin = new Thickness(16, 9, 16, 0)
        };

        var sessionStack = new StackPanel { Spacing = 3 };
        sessionStack.Children.Add(Text("AKTIVE SESSION", 9, FontWeight.Bold, "#93C5FD"));
        sessionStack.Children.Add(Text(session?.Name ?? "Keine Session", 13, FontWeight.Bold, "#F8FAFC"));
        sessionStack.Children.Add(Text(session is null ? "Keine ID" : $"ID: {session.Id}", 10, FontWeight.Normal, "#CBD5E1"));
        grid.Children.Add(CompactCard(sessionStack));

        var localStack = new StackPanel { Spacing = 3 };
        localStack.Children.Add(Text(local is null ? "LOKALER SPIELER" : local.DisplayName.ToUpperInvariant(), 9, FontWeight.Bold, "#93C5FD"));
        localStack.Children.Add(BoundText("LocalPlayerStatus", 11, FontWeight.SemiBold, "#A7F3D0"));
        localStack.Children.Add(BoundText("LocalGameText", 10, FontWeight.Normal, "#CBD5E1"));
        localStack.Children.Add(BoundText("LocalActivePokemonText", 10, FontWeight.Normal, "#CBD5E1"));
        var localCard = CompactCard(localStack);
        Grid.SetColumn(localCard, 1);
        grid.Children.Add(localCard);

        var partnerStack = new StackPanel { Spacing = 5 };
        partnerStack.Children.Add(Text(partner is null ? "MITTSPIELER" : partner.DisplayName.ToUpperInvariant(), 9, FontWeight.Bold, "#93C5FD"));
        partnerStack.Children.Add(Text(partner is null ? "🟡 Nicht verbunden" : "🟡 Lokal eingetragen", 11, FontWeight.SemiBold, "#FBBF24"));
        var partnerStatus = BoundText("PartnerStatus", 10, FontWeight.Normal, "#CBD5E1");
        partnerStatus.TextWrapping = TextWrapping.Wrap;
        partnerStack.Children.Add(partnerStatus);

        _networkStatusText = Text(_networkService.StatusText, 9, FontWeight.Normal, "#94A3B8");
        _networkStatusText.TextWrapping = TextWrapping.Wrap;
        partnerStack.Children.Add(_networkStatusText);

        var buttonGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 6
        };
        buttonGrid.Children.Add(CreateNetworkButton("Host", PrepareHost, session is not null && local is not null, true));
        var joinButton = CreateNetworkButton("Beitreten", PrepareJoin, session is not null && local is not null, false);
        Grid.SetColumn(joinButton, 1);
        buttonGrid.Children.Add(joinButton);
        partnerStack.Children.Add(buttonGrid);

        var partnerCard = CompactCard(partnerStack);
        Grid.SetColumn(partnerCard, 2);
        grid.Children.Add(partnerCard);
        return grid;
    }

    private Button CreateNetworkButton(string label, Action action, bool isEnabled, bool primary)
    {
        var button = new Button
        {
            Content = label,
            IsEnabled = isEnabled,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(8, 5),
            FontSize = 10,
            FontWeight = FontWeight.SemiBold,
            Background = Brush(primary ? "#1D4ED8" : "#172554"),
            Foreground = Brush("#F8FAFC"),
            BorderBrush = Brush(primary ? "#60A5FA" : "#334E8A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(7)
        };
        button.Click += (_, _) => action();
        return button;
    }

    private void PrepareHost() => PrepareNetwork((sessionId, playerName) => _networkService.PrepareHost(sessionId, playerName));
    private void PrepareJoin() => PrepareNetwork((sessionId, playerName) => _networkService.PrepareJoin(sessionId, playerName));

    private void PrepareNetwork(Action<string, string> prepare)
    {
        try
        {
            prepare(_sessionContext?.Session.Id ?? string.Empty, _sessionContext?.LocalPlayer.DisplayName ?? string.Empty);
        }
        catch (Exception ex)
        {
            if (_networkStatusText is not null)
            {
                _networkStatusText.Text = ex.Message;
                _networkStatusText.Foreground = Brush("#FCA5A5");
            }
        }
    }

    private void OnNetworkStatusChanged(object? sender, EventArgs eventArgs)
    {
        Dispatcher.UIThread.Post(() =>
        {
            if (_networkStatusText is not null)
            {
                _networkStatusText.Text = _networkService.StatusText;
                _networkStatusText.Foreground = Brush(
                    _networkService.State == SoulBuddyNetworkState.Connected
                        ? "#A7F3D0"
                        : "#94A3B8");
            }
            RenderParty();
        });
    }

    private void OnPlayerSnapshotReceived(object? sender, NetworkPlayerSnapshot snapshot)
    {
        Dispatcher.UIThread.Post(RenderParty);
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
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 1);
        grid.Children.Add(scroll);
        return grid;
    }

    private Control BuildRightColumn()
    {
        var tabs = new TabControl();
        var livePanel = new StackPanel { Spacing = 8, Margin = new Thickness(2) };
        var liveTitle = BoundText("LiveEncounterTitle", 12, FontWeight.Bold, "#93C5FD");
        livePanel.Children.Add(liveTitle);
        var liveText = BoundText("LiveEncounterText", 12, FontWeight.Normal, "#E2E8F0");
        liveText.TextWrapping = TextWrapping.Wrap;
        liveText.LineHeight = 18;
        livePanel.Children.Add(liveText);
        livePanel.Children.Add(new Border { Height = 1, Background = Brush("#334155"), Margin = new Thickness(0, 4) });
        livePanel.Children.Add(Text("Die Gegner- und Ortsdaten können je nach ROM noch unvollständig sein.", 10, FontWeight.Normal, "#94A3B8"));

        var activity = new ListBox
        {
            ItemsSource = _viewModel.ActivityFeed,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0)
        };
        var detailsPanel = new StackPanel { Spacing = 8, Margin = new Thickness(2) };
        var detailsTitle = BoundText("DetailsTitle", 17, FontWeight.Bold, "#F8FAFC");
        detailsTitle.TextWrapping = TextWrapping.Wrap;
        detailsPanel.Children.Add(detailsTitle);
        var details = BoundText("DetailsText", 11, FontWeight.Normal, "#CBD5E1");
        details.TextWrapping = TextWrapping.Wrap;
        details.LineHeight = 17;
        detailsPanel.Children.Add(details);

        tabs.Items.Add(new TabItem { Header = "Live", Content = new ScrollViewer { Content = livePanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        tabs.Items.Add(new TabItem { Header = "Aktivität", Content = activity });
        tabs.Items.Add(new TabItem { Header = "Details", Content = new ScrollViewer { Content = detailsPanel, VerticalScrollBarVisibility = ScrollBarVisibility.Auto } });
        return tabs;
    }

    private Control SectionHeader(string title, string countBinding)
    {
        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 7)
        };
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
        {
            _storedPanel.Children.Add(PokemonCard(pokemon, false));
        }
    }

    private Control PokemonCard(PokemonCardViewModel pokemon, bool party)
    {
        var small = party && _compact;
        var sprite = new Image
        {
            Width = small ? 34 : 52,
            Height = small ? 34 : 52,
            Stretch = Stretch.Uniform
        };
        var spriteFrame = new Border
        {
            Width = small ? 40 : 58,
            Height = small ? 40 : 58,
            Background = Brush("#18243A"),
            BorderBrush = Brush("#334866"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Child = sprite
        };

        var text = new StackPanel { Spacing = small ? 1 : 3 };
        var name = Text(pokemon.NameLine, small ? 10 : 13, FontWeight.SemiBold, "#F8FAFC");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        text.Children.Add(name);
        var location = Text($"📍 {pokemon.Subtitle}", small ? 8 : 10, FontWeight.Normal, "#94A3B8");
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
            text.Children.Add(Text(pokemon.LevelText, 9, FontWeight.Medium, "#93C5FD"));
        }

        var ownLayout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = small ? 6 : 9,
            MinWidth = 0
        };
        ownLayout.Children.Add(spriteFrame);
        Grid.SetColumn(text, 1);
        ownLayout.Children.Add(text);

        Control content = ownLayout;
        if (party && _networkService.State == SoulBuddyNetworkState.Connected)
        {
            var completeLayout = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("3*,1*"),
                ColumnSpacing = small ? 4 : 7,
                MinWidth = 0
            };
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
            Background = Brush("#0F1829"),
            BorderBrush = Brush("#344763"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8)
        };
        button.Click += (_, _) => _viewModel.SelectPokemon(pokemon);
        _ = LoadSpriteAsync(pokemon.SpeciesId, pokemon.IsShiny, sprite);
        return button;
    }

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
        {
            _ = LoadSpriteAsync(pokemon.LinkedSpeciesId, false, image);
        }
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
        {
            image.Source = visual.Sprite;
        }
    }

    private void ApplyResponsiveLayout()
    {
        var compact = Bounds.Width < 1050 || Bounds.Height < 720;
        if (_compact == compact)
        {
            return;
        }
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

    private static Border CompactCard(Control child) => new()
    {
        Background = Brush("#151F33"),
        BorderBrush = Brush("#2B3C58"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(9),
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
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        _networkService.StatusChanged -= OnNetworkStatusChanged;
        _networkService.PlayerSnapshotReceived -= OnPlayerSnapshotReceived;
        await _networkService.DisposeAsync();
        await _viewModel.DisposeAsync();
        _visualService.Dispose();
    }
}