using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Data;
using Avalonia.Layout;
using Avalonia.Media;
using SoulBuddy.ViewModels;

namespace SoulBuddy.Views;

public sealed class MainWindow : Window
{
    private readonly MainWindowViewModel _viewModel;
    private readonly StackPanel _partyPanel;
    private readonly StackPanel _pokemonPanel;

    public MainWindow()
    {
        Title = "SoulBuddy";
        Width = 1360;
        Height = 820;
        MinWidth = 980;
        MinHeight = 640;
        Background = Brush("#0B1220");

        _viewModel = new MainWindowViewModel();
        DataContext = _viewModel;

        _partyPanel = new StackPanel { Spacing = 12 };
        _pokemonPanel = new StackPanel { Spacing = 10 };

        _viewModel.Party.CollectionChanged += (_, _) => RenderParty();
        _viewModel.StoredPokemon.CollectionChanged += (_, _) => RenderStoredPokemon();

        Content = BuildLayout();
        Opened += OnOpened;
        Closing += OnClosing;
    }

    private Control BuildLayout()
    {
        var root = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*,Auto")
        };

        root.Children.Add(BuildHeader());

        var contentGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("340,*,380"),
            Margin = new Thickness(24, 20),
            ColumnSpacing = 18
        };
        Grid.SetRow(contentGrid, 1);

        contentGrid.Children.Add(CreateCard(BuildSection(
            "Aktuelles Team",
            "PartyCountText",
            _partyPanel)));

        var storedCard = CreateCard(BuildSection(
            "Gespeicherte Pokémon",
            "PokemonCountText",
            _pokemonPanel));
        Grid.SetColumn(storedCard, 1);
        contentGrid.Children.Add(storedCard);

        var detailsCard = CreateCard(BuildDetailsSection());
        Grid.SetColumn(detailsCard, 2);
        contentGrid.Children.Add(detailsCard);

        root.Children.Add(contentGrid);

        var footer = new Border
        {
            Background = Brush("#101A2E"),
            BorderBrush = Brush("#263650"),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(24, 13)
        };

        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        var status = Text("", 14, FontWeight.Medium, "#CBD5E1");
        status.Bind(TextBlock.TextProperty, new Binding("StatusText"));
        footerGrid.Children.Add(status);

        var connection = Text("", 14, FontWeight.SemiBold, "#7DD3FC");
        connection.Bind(TextBlock.TextProperty, new Binding("ConnectionText"));
        Grid.SetColumn(connection, 1);
        footerGrid.Children.Add(connection);

        footer.Child = footerGrid;
        Grid.SetRow(footer, 2);
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
            Padding = new Thickness(26, 18)
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        var titlePanel = new StackPanel { Spacing = 3 };
        titlePanel.Children.Add(Text(
            "SoulBuddy",
            31,
            FontWeight.Bold,
            "#F8FAFC"));
        titlePanel.Children.Add(Text(
            "Dein lokaler Nuzlocke- und SoulLink-Begleiter",
            14,
            FontWeight.Normal,
            "#94A3B8"));
        grid.Children.Add(titlePanel);

        var badge = new Border
        {
            Background = Brush("#172554"),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(15, 8),
            VerticalAlignment = VerticalAlignment.Center,
            Child = Text(
                "PHASE 1 · MVVM",
                12,
                FontWeight.Bold,
                "#93C5FD")
        };
        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);

        header.Child = grid;
        return header;
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
            Margin = new Thickness(0, 0, 0, 16)
        };

        header.Children.Add(Text(
            title,
            20,
            FontWeight.Bold,
            "#F8FAFC"));

        var count = Text("", 13, FontWeight.Medium, "#93C5FD");
        count.Bind(TextBlock.TextProperty, new Binding(countBinding));
        Grid.SetColumn(count, 1);
        header.Children.Add(count);
        grid.Children.Add(header);

        var scrollViewer = new ScrollViewer
        {
            Content = contentPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
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

        grid.Children.Add(Text(
            "Pokémon-Details",
            20,
            FontWeight.Bold,
            "#F8FAFC"));

        var separator = new Border
        {
            Height = 1,
            Background = Brush("#334155"),
            Margin = new Thickness(0, 16, 0, 22)
        };
        Grid.SetRow(separator, 1);
        grid.Children.Add(separator);

        var detailPanel = new StackPanel { Spacing = 16 };

        var title = Text("", 28, FontWeight.Bold, "#F8FAFC");
        title.Bind(TextBlock.TextProperty, new Binding("DetailsTitle"));
        detailPanel.Children.Add(title);

        var details = Text("", 15, FontWeight.Normal, "#CBD5E1");
        details.TextWrapping = TextWrapping.Wrap;
        details.LineHeight = 23;
        details.Bind(TextBlock.TextProperty, new Binding("DetailsText"));
        detailPanel.Children.Add(details);

        var detailScroll = new ScrollViewer
        {
            Content = detailPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
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
            CornerRadius = new CornerRadius(16),
            Padding = new Thickness(20),
            Child = content,
            BoxShadow = new BoxShadows(new BoxShadow
            {
                Blur = 18,
                OffsetY = 4,
                Color = Color.Parse("#33000000")
            })
        };
    }

    private void RenderParty()
    {
        _partyPanel.Children.Clear();

        if (_viewModel.Party.Count == 0)
        {
            _partyPanel.Children.Add(CreateEmptyState(
                "Warte auf Teamdaten vom Emulator …"));
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
            _pokemonPanel.Children.Add(CreateEmptyState(
                "Noch keine Pokémon lokal gespeichert."));
            return;
        }

        foreach (var pokemon in _viewModel.StoredPokemon)
        {
            _pokemonPanel.Children.Add(CreatePokemonCard(pokemon));
        }
    }

    private Control CreatePokemonCard(PokemonCardViewModel pokemon)
    {
        var panel = new StackPanel { Spacing = 8 };

        panel.Children.Add(Text(
            pokemon.NameLine,
            16,
            FontWeight.SemiBold,
            "#F8FAFC"));

        var infoGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto")
        };
        infoGrid.Children.Add(Text(
            pokemon.LevelText,
            12,
            FontWeight.Medium,
            "#93C5FD"));

        var subtitle = Text(
            pokemon.Subtitle,
            12,
            FontWeight.Normal,
            "#94A3B8");
        subtitle.Margin = new Thickness(12, 0);
        Grid.SetColumn(subtitle, 1);
        infoGrid.Children.Add(subtitle);

        var hpText = Text(
            pokemon.HpText,
            12,
            FontWeight.Medium,
            "#CBD5E1");
        Grid.SetColumn(hpText, 2);
        infoGrid.Children.Add(hpText);
        panel.Children.Add(infoGrid);

        panel.Children.Add(new ProgressBar
        {
            Minimum = 0,
            Maximum = 100,
            Value = pokemon.HpPercentage,
            Height = 7,
            CornerRadius = new CornerRadius(4),
            Background = Brush("#263650"),
            Foreground = Brush(GetHpColor(pokemon.HpPercentage))
        });

        var button = new Button
        {
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(14, 13),
            Background = Brush("#0F1829"),
            BorderBrush = Brush("#344763"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(12),
            Content = panel
        };
        button.Click += (_, _) => _viewModel.SelectPokemon(pokemon);
        return button;
    }

    private static Control CreateEmptyState(string value)
    {
        return new Border
        {
            Background = Brush("#0F1829"),
            CornerRadius = new CornerRadius(12),
            Padding = new Thickness(16),
            Child = Text(
                value,
                13,
                FontWeight.Normal,
                "#94A3B8")
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
        RenderParty();
        RenderStoredPokemon();
        await _viewModel.InitializeAsync();
    }

    private async void OnClosing(
        object? sender,
        WindowClosingEventArgs eventArgs)
    {
        await _viewModel.DisposeAsync();
    }
}
