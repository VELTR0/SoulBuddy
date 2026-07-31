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
    private readonly StackPanel _partyPanel;
    private readonly StackPanel _pokemonPanel;
    private Grid _contentGrid = null!;
    private Grid _sessionGrid = null!;
    private bool _compactLayout;

    public MainWindow(SessionContext? sessionContext = null)
    {
        _sessionContext = sessionContext;
        Title = sessionContext is null
            ? "SoulBuddy"
            : $"SoulBuddy · {sessionContext.Session.Name} · {sessionContext.LocalPlayer.DisplayName}";
        Width = 1360;
        Height = 900;
        MinWidth = 760;
        MinHeight = 620;
        Background = Brush("#0B1220");

        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;

        _partyPanel = new StackPanel { Spacing = 10 };
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
            ColumnDefinitions = new ColumnDefinitions("1.05*,1.35*,1*"),
            Margin = new Thickness(20, 14, 20, 18),
            ColumnSpacing = 14
        };
        Grid.SetRow(_contentGrid, 2);

        _contentGrid.Children.Add(CreateCard(BuildSection(
            "Aktuelles Team",
            "PartyCountText",
            _partyPanel)));

        var storedCard = CreateCard(BuildSection(
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
            Padding = new Thickness(20, 11)
        };

        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        var status = Text("", 13, FontWeight.Medium, "#CBD5E1");
        status.TextTrimming = TextTrimming.CharacterEllipsis;
        status.Bind(TextBlock.TextProperty, new Binding("StatusText"));
        footerGrid.Children.Add(status);

        var connection = Text("", 13, FontWeight.SemiBold, "#7DD3FC");
        connection.Margin = new Thickness(12, 0, 0, 0);
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
            Padding = new Thickness(22, 14)
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        var titlePanel = new StackPanel { Spacing = 2 };
        titlePanel.Children.Add(Text("SoulBuddy", 28, FontWeight.Bold, "#F8FAFC"));
        titlePanel.Children.Add(Text(
            "Dein lokaler Nuzlocke- und SoulLink-Begleiter",
            13,
            FontWeight.Normal,
            "#94A3B8"));
        grid.Children.Add(titlePanel);

        var badge = new Border
        {
            Background = Brush("#172554"),
            CornerRadius = new CornerRadius(17),
            Padding = new Thickness(13, 7),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text("PHASE 3A · SESSION", 11, FontWeight.Bold, "#93C5FD")
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
            ColumnSpacing = 12,
            Margin = new Thickness(20, 14, 20, 0)
        };

        var sessionDetails = new StackPanel { Spacing = 5 };
        sessionDetails.Children.Add(Text("AKTIVE SESSION", 10, FontWeight.Bold, "#93C5FD"));
        var sessionName = Text(session?.Name ?? "Keine Session geladen", 17, FontWeight.Bold, "#F8FAFC");
        sessionName.TextWrapping = TextWrapping.Wrap;
        sessionDetails.Children.Add(sessionName);
        sessionDetails.Children.Add(Text(
            session is null ? "Session-ID nicht verfügbar" : $"ID: {session.Id}",
            12,
            FontWeight.Normal,
            "#CBD5E1"));
        panel.Children.Add(CreateCompactCard(sessionDetails));

        var players = new StackPanel { Spacing = 5 };
        players.Children.Add(Text("TEILNEHMER", 10, FontWeight.Bold, "#93C5FD"));
        var localPlayerText = Text(
            localPlayer is null ? "Du: nicht geladen" : $"Du: {localPlayer.DisplayName} · Slot {localPlayer.Slot}",
            13,
            FontWeight.SemiBold,
            "#F8FAFC");
        localPlayerText.TextWrapping = TextWrapping.Wrap;
        players.Children.Add(localPlayerText);
        var partnerText = Text(
            partner is null ? "Mitspieler: noch nicht erkannt" : $"Mitspieler: {partner.DisplayName} · Slot {partner.Slot}",
            12,
            FontWeight.Normal,
            partner is null ? "#FBBF24" : "#A7F3D0");
        partnerText.TextWrapping = TextWrapping.Wrap;
        players.Children.Add(partnerText);
        var playersCard = CreateCompactCard(players);
        Grid.SetColumn(playersCard, 1);
        panel.Children.Add(playersCard);

        var remoteState = new StackPanel { Spacing = 5 };
        remoteState.Children.Add(Text("VERBINDUNG & PARTNERTEAM", 10, FontWeight.Bold, "#93C5FD"));
        var networkState = Text(
            "Lokal gespeichert · Netzwerk noch nicht verbunden",
            13,
            FontWeight.SemiBold,
            "#FBBF24");
        networkState.TextWrapping = TextWrapping.Wrap;
        remoteState.Children.Add(networkState);
        var remoteDescription = Text(
            partner is null
                ? "Es wurden noch keine Daten eines Mitspielers empfangen."
                : $"{partner.DisplayName} ist lokal eingetragen. Pokémon-Daten wurden noch nicht synchronisiert.",
            12,
            FontWeight.Normal,
            "#CBD5E1");
        remoteDescription.TextWrapping = TextWrapping.Wrap;
        remoteState.Children.Add(remoteDescription);
        var phaseNote = Text(
            "Partner-Pokémon erscheinen nach Aktivierung des Datenaustauschs in Phase 3B.",
            11,
            FontWeight.Normal,
            "#7C8BA1");
        phaseNote.TextWrapping = TextWrapping.Wrap;
        remoteState.Children.Add(phaseNote);
        var remoteCard = CreateCompactCard(remoteState);
        Grid.SetColumn(remoteCard, 2);
        panel.Children.Add(remoteCard);

        return panel;
    }

    private Control BuildSection(
        string title,
        string countBinding,
        StackPanel contentPanel)
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 12)
        };

        var titleText = Text(title, 18, FontWeight.Bold, "#F8FAFC");
        titleText.TextWrapping = TextWrapping.Wrap;
        header.Children.Add(titleText);

        var count = Text("", 12, FontWeight.Medium, "#93C5FD");
        count.Margin = new Thickness(8, 0, 0, 0);
        count.Bind(TextBlock.TextProperty, new Binding(countBinding));
        Grid.SetColumn(count, 1);
        header.Children.Add(count);
        grid.Children.Add(header);

        var scrollViewer = new ScrollViewer
        {
            Content = contentPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(scrollViewer, 1);
        grid.Children.Add(scrollViewer);

        return grid;
    }

    private Control BuildDetailsSection()
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,Auto,*")
        };

        grid.Children.Add(Text("Pokémon-Details", 18, FontWeight.Bold, "#F8FAFC"));

        var separator = new Border
        {
            Height = 1,
            Background = Brush("#334155"),
            Margin = new Thickness(0, 12, 0, 16)
        };
        Grid.SetRow(separator, 1);
        grid.Children.Add(separator);

        var detailPanel = new StackPanel { Spacing = 12 };

        var title = Text("", 24, FontWeight.Bold, "#F8FAFC");
        title.TextWrapping = TextWrapping.Wrap;
        title.Bind(TextBlock.TextProperty, new Binding("DetailsTitle"));
        detailPanel.Children.Add(title);

        var details = Text("", 14, FontWeight.Normal, "#CBD5E1");
        details.TextWrapping = TextWrapping.Wrap;
        details.LineHeight = 21;
        details.Bind(TextBlock.TextProperty, new Binding("DetailsText"));
        detailPanel.Children.Add(details);

        var detailScroll = new ScrollViewer
        {
            Content = detailPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
        };
        Grid.SetRow(detailScroll, 2);
        grid.Children.Add(detailScroll);

        return grid;
    }

    private static Border CreateCard(Control content)
    {
        return new Border
        {
            Background = Brush("#151F33"),
            BorderBrush = Brush("#2B3C58"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(16),
            Child = content,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = 15,
                OffsetY = 3,
                Color = Color.Parse("#33000000")
            })
        };
    }

    private static Border CreateCompactCard(Control content)
    {
        return new Border
        {
            Background = Brush("#151F33"),
            BorderBrush = Brush("#2B3C58"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(13),
            Child = content
        };
    }

    private void ApplyResponsiveLayout()
    {
        if (_contentGrid is null || _sessionGrid is null)
        {
            return;
        }

        var compact = Bounds.Width < 1120;
        var veryCompact = Bounds.Width < 900;

        _contentGrid.ColumnDefinitions = veryCompact
            ? new ColumnDefinitions("1*,1.05*,1*")
            : new ColumnDefinitions("1.05*,1.35*,1*");
        _contentGrid.ColumnSpacing = compact ? 9 : 14;
        _contentGrid.Margin = compact
            ? new Thickness(12, 10, 12, 12)
            : new Thickness(20, 14, 20, 18);

        _sessionGrid.ColumnDefinitions = veryCompact
            ? new ColumnDefinitions("1*,1*,1.15*")
            : new ColumnDefinitions("1.05*,1*,1.35*");
        _sessionGrid.ColumnSpacing = compact ? 8 : 12;
        _sessionGrid.Margin = compact
            ? new Thickness(12, 10, 12, 0)
            : new Thickness(20, 14, 20, 0);

        if (_compactLayout == compact)
        {
            return;
        }

        _compactLayout = compact;
        _partyPanel.Spacing = compact ? 7 : 10;
        _pokemonPanel.Spacing = compact ? 7 : 9;
        RenderParty();
        RenderStoredPokemon();
    }

    private void RenderParty()
    {
        _partyPanel.Children.Clear();

        if (_viewModel.Party.Count == 0)
        {
            _partyPanel.Children.Add(CreateEmptyState("Warte auf Teamdaten vom Emulator …"));
            return;
        }

        foreach (var pokemon in _viewModel.Party)
        {
            _partyPanel.Children.Add(CreatePokemonCard(pokemon));
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
            _pokemonPanel.Children.Add(CreatePokemonCard(pokemon));
        }
    }

    private Control CreatePokemonCard(PokemonCardViewModel pokemon)
    {
        var spriteSize = _compactLayout ? 58d : 72d;
        var frameSize = _compactLayout ? 66d : 82d;
        var contentSpacing = _compactLayout ? 4d : 6d;
        var cardPadding = _compactLayout ? 7d : 10d;
        var columnSpacing = _compactLayout ? 8d : 11d;

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
            CornerRadius = new CornerRadius(_compactLayout ? 10 : 12),
            Background = Brush("#18243A"),
            BorderBrush = Brush("#334866"),
            BorderThickness = new Thickness(1),
            Child = sprite
        };

        var content = new StackPanel { Spacing = contentSpacing };

        var nameGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto,Auto")
        };
        var name = Text(
            pokemon.NameLine,
            _compactLayout ? 13 : 15,
            FontWeight.SemiBold,
            "#F8FAFC");
        name.TextTrimming = TextTrimming.CharacterEllipsis;
        nameGrid.Children.Add(name);

        var gender = Text(
            pokemon.GenderSymbol,
            _compactLayout ? 14 : 16,
            FontWeight.Bold,
            pokemon.Gender == "Weiblich" ? "#F9A8D4" : "#7DD3FC");
        gender.Margin = new Thickness(_compactLayout ? 4 : 6, 0, 0, 0);
        Grid.SetColumn(gender, 1);
        nameGrid.Children.Add(gender);

        var shiny = Text(
            pokemon.ShinySymbol,
            _compactLayout ? 14 : 16,
            FontWeight.Bold,
            "#FDE047");
        shiny.Margin = new Thickness(5, 0, 0, 0);
        Grid.SetColumn(shiny, 2);
        nameGrid.Children.Add(shiny);
        content.Children.Add(nameGrid);

        var typePanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = _compactLayout ? 4 : 5,
            MinHeight = _compactLayout ? 18 : 21
        };
        content.Children.Add(typePanel);

        var infoGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        infoGrid.Children.Add(Text(
            pokemon.LevelText,
            _compactLayout ? 10 : 11,
            FontWeight.Medium,
            "#93C5FD"));

        var subtitle = Text(
            pokemon.Subtitle,
            _compactLayout ? 10 : 11,
            FontWeight.Normal,
            "#94A3B8");
        subtitle.Margin = new Thickness(_compactLayout ? 6 : 9, 0);
        subtitle.TextTrimming = TextTrimming.CharacterEllipsis;
        Grid.SetColumn(subtitle, 1);
        infoGrid.Children.Add(subtitle);

        var hpText = Text(
            pokemon.HpText,
            _compactLayout ? 10 : 11,
            FontWeight.Medium,
            "#CBD5E1");
        Grid.SetColumn(hpText, 2);
        infoGrid.Children.Add(hpText);
        content.Children.Add(infoGrid);

        content.Children.Add(new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = pokemon.HpPercentage,
            Height = _compactLayout ? 5 : 6,
            CornerRadius = new CornerRadius(3),
            Background = Brush("#263650"),
            Foreground = Brush(GetHpColor(pokemon.HpPercentage))
        });

        if (!_compactLayout && !string.IsNullOrWhiteSpace(pokemon.TraitLine))
        {
            var traits = Text(pokemon.TraitLine, 10, FontWeight.Normal, "#A8B5C7");
            traits.TextWrapping = TextWrapping.Wrap;
            content.Children.Add(traits);
        }

        var cardGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*"),
            ColumnSpacing = columnSpacing
        };
        cardGrid.Children.Add(spriteFrame);
        Grid.SetColumn(content, 1);
        cardGrid.Children.Add(content);

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(cardPadding),
            Background = Brush("#0F1829"),
            BorderBrush = Brush("#344763"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(_compactLayout ? 9 : 11),
            Content = cardGrid
        };
        button.Click += (_, _) => _viewModel.SelectPokemon(pokemon);

        _ = LoadVisualsAsync(pokemon, sprite, typePanel);
        return button;
    }

    private async Task LoadVisualsAsync(
        PokemonCardViewModel pokemon,
        Image sprite,
        StackPanel typePanel)
    {
        var visualData = await _visualService.GetAsync(
            pokemon.SpeciesId,
            pokemon.IsShiny);

        if (visualData.Sprite is not null)
        {
            sprite.Source = visualData.Sprite;
        }

        typePanel.Children.Clear();
        foreach (var type in visualData.Types)
        {
            typePanel.Children.Add(CreateTypeBadge(type, _compactLayout));
        }
    }

    private static Border CreateTypeBadge(string type, bool compact)
    {
        return new Border
        {
            Background = Brush(GetTypeColor(type)),
            CornerRadius = new CornerRadius(compact ? 7 : 9),
            Padding = compact
                ? new Thickness(6, 2)
                : new Thickness(8, 2),
            Child = Text(type, compact ? 8 : 9, FontWeight.Bold, "#FFFFFF")
        };
    }

    private static string GetTypeColor(string type)
    {
        return type switch
        {
            "Normal" => "#7C8492",
            "Feuer" => "#E25835",
            "Wasser" => "#3977C5",
            "Elektro" => "#C9A613",
            "Pflanze" => "#4C9A51",
            "Eis" => "#4BA3B1",
            "Kampf" => "#B43D45",
            "Gift" => "#8E4AA5",
            "Boden" => "#A8783F",
            "Flug" => "#748CC5",
            "Psycho" => "#D85683",
            "Käfer" => "#7F961D",
            "Gestein" => "#958143",
            "Geist" => "#5D568B",
            "Drache" => "#5E51B5",
            "Unlicht" => "#514A52",
            "Stahl" => "#657D8C",
            "Fee" => "#C76B9C",
            _ => "#475569"
        };
    }

    private static Control CreateEmptyState(string value)
    {
        var text = Text(value, 12, FontWeight.Normal, "#94A3B8");
        text.TextWrapping = TextWrapping.Wrap;

        return new Border
        {
            Background = Brush("#0F1829"),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(13),
            Child = text
        };
    }

    private static string GetHpColor(double percentage)
    {
        if (percentage <= 20)
        {
            return "#F87171";
        }

        if (percentage <= 50)
        {
            return "#FBBF24";
        }

        return "#4ADE80";
    }

    private static TextBlock Text(
        string value,
        double fontSize,
        FontWeight fontWeight,
        string color)
    {
        return new TextBlock
        {
            Text = value,
            FontSize = fontSize,
            FontWeight = fontWeight,
            Foreground = Brush(color)
        };
    }

    private static SolidColorBrush Brush(string color)
    {
        return new SolidColorBrush(Color.Parse(color));
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        ApplyResponsiveLayout();
        RenderParty();
        RenderStoredPokemon();
        await _viewModel.InitializeAsync();
    }

    private async void OnClosing(
        object? sender,
        WindowClosingEventArgs eventArgs)
    {
        await _viewModel.DisposeAsync();
        _visualService.Dispose();
    }
}
