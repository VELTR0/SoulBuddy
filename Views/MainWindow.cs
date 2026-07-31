using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using SoulBuddy.Models;
using SoulBuddy.Services;
using SoulBuddy.ViewModels;

namespace SoulBuddy.Views;

public sealed class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly PokemonVisualService _visualService = new();
    private readonly SessionContext? _sessionContext;
    private readonly Grid _partyPanel;
    private readonly StackPanel _pokemonPanel;
    private Grid _contentGrid = null!;
    private Grid _sessionGrid = null!;
    private bool _compactLayout;
    private bool _veryCompactLayout;

    public MainWindow(SessionContext? sessionContext = null)
    {
        _sessionContext = sessionContext;
        Title = sessionContext is null
            ? "SoulBuddy"
            : $"SoulBuddy · {sessionContext.Session.Name} · {sessionContext.LocalPlayer.DisplayName}";
        Width = 1360;
        Height = 900;
        MinWidth = 560;
        MinHeight = 460;
        Background = Brush("#0B1220");

        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;

        _partyPanel = new Grid
        {
            RowDefinitions = new RowDefinitions("*,*,*,*,*,*"),
            RowSpacing = 6
        };
        _pokemonPanel = new StackPanel { Spacing = 9 };

        _viewModel.Party.CollectionChanged += (_, _) => RenderParty();
        _viewModel.StoredPokemon.CollectionChanged += (_, _) => RenderStoredPokemon();

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

        _sessionGrid = BuildSessionPanel();
        Grid.SetRow(_sessionGrid, 1);
        root.Children.Add(_sessionGrid);

        _contentGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.15*,1.35*,1*"),
            Margin = new Thickness(20, 14, 20, 18),
            ColumnSpacing = 14
        };
        Grid.SetRow(_contentGrid, 2);

        _contentGrid.Children.Add(CreateCard(BuildPartySection()));

        var storedCard = CreateCard(BuildScrollableSection(
            "Gespeicherte Pokémon",
            "PokemonCountText",
            _pokemonPanel));
        Grid.SetColumn(storedCard, 1);
        _contentGrid.Children.Add(storedCard);

        var detailsCard = CreateCard(BuildDetailsSection());
        Grid.SetColumn(detailsCard, 2);
        _contentGrid.Children.Add(detailsCard);

        root.Children.Add(_contentGrid);

        var footer = new Border
        {
            Background = Brush("#101A2E"),
            BorderBrush = Brush("#263650"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(16, 8)
        };

        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        var status = Text("", 12, FontWeight.Medium, "#CBD5E1");
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.Bind(TextBlock.TextProperty, new Binding("StatusText"));
        footerGrid.Children.Add(status);

        var connection = Text("", 12, FontWeight.SemiBold, "#7DD3FC");
        connection.Margin = new Thickness(10, 0, 0, 0);
        connection.Bind(TextBlock.TextProperty, new Binding("ConnectionText"));
        Grid.SetColumn(connection, 1);
        footerGrid.Children.Add(connection);

        footer.Child = footerGrid;
        Grid.SetRow(footer, 3);
        root.Children.Add(footer);

        return root;
    }

    private Control BuildHeader()
    {
        var header = new Border
        {
            Background = Brush("#101A2E"),
            BorderBrush = Brush("#263650"),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(18, 10)
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        var titlePanel = new StackPanel { Spacing = 1 };
        titlePanel.Children.Add(Text("SoulBuddy", 24, FontWeight.Bold, "#F8FAFC"));
        var subtitle = Text(
            "Dein lokaler Nuzlocke- und SoulLink-Begleiter",
            12,
            FontWeight.Normal,
            "#94A3B8");
        subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        titlePanel.Children.Add(subtitle);
        grid.Children.Add(titlePanel);

        var badge = new Border
        {
            Background = Brush("#172554"),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(11, 6),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text("PHASE 3A", 10, FontWeight.Bold, "#93C5FD")
        };
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        header.Child = grid;
        return header;
    }

    private Grid BuildSessionPanel()
    {
        var session = _sessionContext?.Session;
        var localPlayer = _sessionContext?.LocalPlayer;
        var partner = session?.Players.FirstOrDefault(player => player.Id != localPlayer?.Id);

        var panel = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1.05*,1*,1.35*"),
            ColumnSpacing = 10,
            Margin = new Thickness(20, 10, 20, 0)
        };

        var sessionDetails = new StackPanel { Spacing = 3 };
        sessionDetails.Children.Add(Text("AKTIVE SESSION", 9, FontWeight.Bold, "#93C5FD"));
        var sessionName = Text(session?.Name ?? "Keine Session", 14, FontWeight.Bold, "#F8FAFC");
        sessionName.TextTrimming = TextTrimming.CharacterEllipsis;
        sessionDetails.Children.Add(sessionName);
        var sessionId = Text(
            session is null ? "Keine ID" : $"ID: {session.Id}",
            11,
            FontWeight.Normal,
            "#CBD5E1");
        sessionId.TextTrimming = TextTrimming.CharacterEllipsis;
        sessionDetails.Children.Add(sessionId);
        panel.Children.Add(CreateCompactCard(sessionDetails));

        var players = new StackPanel { Spacing = 3 };
        players.Children.Add(Text("TEILNEHMER", 9, FontWeight.Bold, "#93C5FD"));
        var local = Text(
            localPlayer is null ? "Du: nicht geladen" : $"Du: {localPlayer.DisplayName}",
            11,
            FontWeight.SemiBold,
            "#F8FAFC");
        local.TextTrimming = TextTrimming.CharacterEllipsis;
        players.Children.Add(local);
        var partnerText = Text(
            partner is null ? "Partner: nicht verbunden" : $"Partner: {partner.DisplayName}",
            11,
            FontWeight.Normal,
            partner is null ? "#FBBF24" : "#A7F3D0");
        partnerText.TextTrimming = TextTrimming.CharacterEllipsis;
        players.Children.Add(partnerText);
        var playersCard = CreateCompactCard(players);
        Grid.SetColumn(playersCard, 1);
        panel.Children.Add(playersCard);

        var remoteState = new StackPanel { Spacing = 3 };
        remoteState.Children.Add(Text("VERBINDUNG", 9, FontWeight.Bold, "#93C5FD"));
        remoteState.Children.Add(Text(
            "Lokal gespeichert · Netzwerk folgt in Phase 3B",
            11,
            FontWeight.SemiBold,
            "#FBBF24"));
        var remoteInfo = Text(
            partner is null
                ? "Noch keine Partnerdaten empfangen."
                : $"{partner.DisplayName} ist lokal eingetragen.",
            10,
            FontWeight.Normal,
            "#CBD5E1");
        remoteInfo.TextTrimming = TextTrimming.CharacterEllipsis;
        remoteState.Children.Add(remoteInfo);
        var remoteCard = CreateCompactCard(remoteState);
        Grid.SetColumn(remoteCard, 2);
        panel.Children.Add(remoteCard);

        return panel;
    }

    private Control BuildPartySection()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };

        grid.Children.Add(BuildSectionHeader("Aktuelles Team", "PartyCountText"));
        Grid.SetRow(_partyPanel, 1);
        grid.Children.Add(_partyPanel);
        return grid;
    }

    private Control BuildScrollableSection(
        string title,
        string countBinding,
        Control content)
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };

        grid.Children.Add(BuildSectionHeader(title, countBinding));

        var scrollViewer = new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scrollViewer, 1);
        grid.Children.Add(scrollViewer);
        return grid;
    }

    private Control BuildSectionHeader(string title, string countBinding)
    {
        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 8)
        };

        var titleText = Text(title, 16, FontWeight.Bold, "#F8FAFC");
        titleText.TextTrimming = TextTrimming.CharacterEllipsis;
        header.Children.Add(titleText);

        var count = Text("", 11, FontWeight.Medium, "#93C5FD");
        count.Margin = new Thickness(6, 0, 0, 0);
        count.Bind(TextBlock.TextProperty, new Binding(countBinding));
        Grid.SetColumn(count, 1);
        header.Children.Add(count);
        return header;
    }

    private Control BuildDetailsSection()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*")
        };

        grid.Children.Add(Text("Pokémon-Details", 16, FontWeight.Bold, "#F8FAFC"));

        var separator = new Border
        {
            Height = 1,
            Background = Brush("#334155"),
            Margin = new Thickness(0, 9, 0, 11)
        };
        Grid.SetRow(separator, 1);
        grid.Children.Add(separator);

        var detailPanel = new StackPanel { Spacing = 9 };
        var title = Text("", 20, FontWeight.Bold, "#F8FAFC");
        title.TextWrapping = TextWrapping.Wrap;
        title.Bind(TextBlock.TextProperty, new Binding("DetailsTitle"));
        detailPanel.Children.Add(title);

        var details = Text("", 12, FontWeight.Normal, "#CBD5E1");
        details.TextWrapping = TextWrapping.Wrap;
        details.LineHeight = 18;
        details.Bind(TextBlock.TextProperty, new Binding("DetailsText"));
        detailPanel.Children.Add(details);

        var scroll = new ScrollViewer
        {
            Content = detailPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scroll, 2);
        grid.Children.Add(scroll);
        return grid;
    }

    private static Border CreateCard(Control content) => new()
    {
        Background = Brush("#151F33"),
        BorderBrush = Brush("#2B3C58"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(12),
        Padding = new Thickness(12),
        Child = content
    };

    private static Border CreateCompactCard(Control content) => new()
    {
        Background = Brush("#151F33"),
        BorderBrush = Brush("#2B3C58"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(10),
        Padding = new Thickness(10),
        Child = content
    };

    private void ApplyResponsiveLayout()
    {
        if (_contentGrid is null || _sessionGrid is null)
        {
            return;
        }

        var compact = Bounds.Width < 1080 || Bounds.Height < 760;
        var veryCompact = Bounds.Width < 760 || Bounds.Height < 600;

        _contentGrid.ColumnDefinitions = veryCompact
            ? new ColumnDefinitions("1.25*,1*,0.9*")
            : new ColumnDefinitions("1.15*,1.35*,1*");
        _contentGrid.ColumnSpacing = veryCompact ? 6 : compact ? 9 : 14;
        _contentGrid.Margin = veryCompact
            ? new Thickness(7, 6, 7, 7)
            : compact
                ? new Thickness(11, 8, 11, 10)
                : new Thickness(20, 14, 20, 18);

        _sessionGrid.ColumnDefinitions = veryCompact
            ? new ColumnDefinitions("1*,1*,1*")
            : new ColumnDefinitions("1.05*,1*,1.35*");
        _sessionGrid.ColumnSpacing = veryCompact ? 5 : compact ? 7 : 10;
        _sessionGrid.Margin = veryCompact
            ? new Thickness(7, 6, 7, 0)
            : compact
                ? new Thickness(11, 8, 11, 0)
                : new Thickness(20, 10, 20, 0);

        if (_compactLayout == compact && _veryCompactLayout == veryCompact)
        {
            return;
        }

        _compactLayout = compact;
        _veryCompactLayout = veryCompact;
        _partyPanel.RowSpacing = veryCompact ? 2 : compact ? 4 : 6;
        _pokemonPanel.Spacing = veryCompact ? 4 : compact ? 6 : 9;
        RenderParty();
        RenderStoredPokemon();
    }

    private void RenderParty()
    {
        _partyPanel.Children.Clear();

        for (var slot = 0; slot < 6; slot++)
        {
            Control card = slot < _viewModel.Party.Count
                ? CreatePokemonCard(_viewModel.Party[slot], true)
                : CreateEmptyPartySlot(slot + 1);

            Grid.SetRow(card, slot);
            _partyPanel.Children.Add(card);
        }
    }

    private void RenderStoredPokemon()
    {
        _pokemonPanel.Children.Clear();

        if (_viewModel.StoredPokemon.Count == 0)
        {
            _pokemonPanel.Children.Add(CreateEmptyState("Noch keine Pokémon lokal gespeichert."));
            return;
        }

        foreach (var pokemon in _viewModel.StoredPokemon)
        {
            _pokemonPanel.Children.Add(CreatePokemonCard(pokemon, false));
        }
    }

    private Control CreatePokemonCard(PokemonCardViewModel pokemon, bool isPartyCard)
    {
        var tiny = isPartyCard && _veryCompactLayout;
        var compact = isPartyCard && (_compactLayout || _veryCompactLayout);
        var spriteSize = tiny ? 34d : compact ? 44d : 62d;
        var frameSize = tiny ? 40d : compact ? 50d : 70d;
        var padding = tiny ? 3d : compact ? 5d : 8d;
        var spacing = tiny ? 2d : compact ? 3d : 5d;

        var sprite = new Image
        {
            Width = spriteSize,
            Height = spriteSize,
            Stretch = Stretch.Uniform,
            VerticalAlignment = VerticalAlignment.Center
        };

        var spriteFrame = new Border
        {
            Width = frameSize,
            Height = frameSize,
            CornerRadius = new CornerRadius(tiny ? 7 : 9),
            Background = Brush("#18243A"),
            BorderBrush = Brush("#334866"),
            BorderThickness = new Thickness(1),
            Child = sprite
        };

        var content = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,Auto"),
            RowSpacing = spacing,
            VerticalAlignment = VerticalAlignment.Center
        };

        var nameGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto")
        };
        var name = Text(
            pokemon.NameLine,
            tiny ? 10 : compact ? 11 : 14,
            FontWeight.SemiBold,
            "#F8FAFC");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        nameGrid.Children.Add(name);

        var gender = Text(
            pokemon.GenderSymbol,
            tiny ? 10 : compact ? 12 : 15,
            FontWeight.Bold,
            pokemon.Gender == "Weiblich" ? "#F9A8D4" : "#7DD3FC");
        gender.Margin = new Thickness(4, 0, 0, 0);
        Grid.SetColumn(gender, 1);
        nameGrid.Children.Add(gender);

        var shiny = Text(
            pokemon.ShinySymbol,
            tiny ? 10 : compact ? 12 : 15,
            FontWeight.Bold,
            "#FDE047");
        shiny.Margin = new Thickness(3, 0, 0, 0);
        Grid.SetColumn(shiny, 2);
        nameGrid.Children.Add(shiny);
        content.Children.Add(nameGrid);

        var soulLinkGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };
        var location = Text(
            $"📍 {pokemon.Subtitle}",
            tiny ? 8 : compact ? 9 : 10,
            FontWeight.Normal,
            "#94A3B8");
        location.TextTrimming = TextTrimming.CharacterEllipsis;
        soulLinkGrid.Children.Add(location);

        var partner = Text(
            "🔗 noch nicht verknüpft",
            tiny ? 8 : compact ? 9 : 10,
            FontWeight.Medium,
            "#FBBF24");
        partner.Margin = new Thickness(5, 0, 0, 0);
        partner.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(partner, 1);
        soulLinkGrid.Children.Add(partner);
        Grid.SetRow(soulLinkGrid, 1);
        content.Children.Add(soulLinkGrid);

        var statusGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto"),
            ColumnSpacing = 5,
            MinWidth = 0
        };
        statusGrid.Children.Add(Text(
            pokemon.LevelText,
            tiny ? 8 : compact ? 9 : 10,
            FontWeight.Medium,
            "#93C5FD"));

        var hpBar = new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = pokemon.HpPercentage,
            MinWidth = 0,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Height = tiny ? 4 : 5,
            CornerRadius = new CornerRadius(3),
            Background = Brush("#263650"),
            Foreground = Brush(GetHpColor(pokemon.HpPercentage)),
            VerticalAlignment = VerticalAlignment.Center
        };
        Grid.SetColumn(hpBar, 1);
        statusGrid.Children.Add(hpBar);

        var hpText = Text(
            pokemon.HpText,
            tiny ? 8 : compact ? 9 : 10,
            FontWeight.Medium,
            "#CBD5E1");
        hpText.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(hpText, 2);
        statusGrid.Children.Add(hpText);
        Grid.SetRow(statusGrid, 2);
        content.Children.Add(statusGrid);

        var cardGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = tiny ? 5 : compact ? 7 : 10
        };
        cardGrid.Children.Add(spriteFrame);
        Grid.SetColumn(content, 1);
        cardGrid.Children.Add(content);

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Center,
            Padding = new Thickness(padding),
            Background = Brush("#0F1829"),
            BorderBrush = Brush("#344763"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(tiny ? 7 : 9),
            Content = cardGrid
        };
        button.Click += (_, _) => _viewModel.SelectPokemon(pokemon);

        _ = LoadSpriteAsync(pokemon, sprite);
        return button;
    }

    private Control CreateEmptyPartySlot(int slot)
    {
        var text = Text(
            $"Teamplatz {slot} · frei",
            _veryCompactLayout ? 9 : _compactLayout ? 10 : 11,
            FontWeight.Medium,
            "#64748B");

        return new Border
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch,
            Background = Brush("#0F1829"),
            BorderBrush = Brush("#263650"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(8),
            Padding = new Thickness(_veryCompactLayout ? 4 : 7),
            Child = text
        };
    }

    private async Task LoadSpriteAsync(PokemonCardViewModel pokemon, Image sprite)
    {
        var visualData = await _visualService.GetAsync(pokemon.SpeciesId, pokemon.IsShiny);
        if (visualData.Sprite is not null)
        {
            sprite.Source = visualData.Sprite;
        }
    }

    private static Control CreateEmptyState(string value)
    {
        var text = Text(value, 11, FontWeight.Normal, "#94A3B8");
        text.TextWrapping = TextWrapping.Wrap;
        return new Border
        {
            Background = Brush("#0F1829"),
            CornerRadius = new CornerRadius(9),
            Padding = new Thickness(11),
            Child = text
        };
    }

    private static string GetHpColor(double percentage)
    {
        if (percentage <= 20)
        {
            return "#F87171";
        }

        return percentage <= 50 ? "#FBBF24" : "#4ADE80";
    }

    private static TextBlock Text(
        string value,
        double fontSize,
        FontWeight fontWeight,
        string color) => new()
    {
        Text = value,
        FontSize = fontSize,
        FontWeight = fontWeight,
        Foreground = Brush(color)
    };

    private static SolidColorBrush Brush(string color) =>
        new(Color.Parse(color));

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        ApplyResponsiveLayout();
        RenderParty();
        RenderStoredPokemon();
        await _viewModel.InitializeAsync();
    }

    private async void OnClosing(object? sender, WindowClosingEventArgs eventArgs)
    {
        await _viewModel.DisposeAsync();
        _visualService.Dispose();
    }
}
