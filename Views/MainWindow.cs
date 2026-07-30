using Avalonia;
using Avalonia.Controls;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using SoulBuddy.Data;
using SoulBuddy.Models;
using SoulBuddy.Services;

namespace SoulBuddy.Views;

public sealed class MainWindow : Window
{
    private readonly TextBlock _statusText;
    private readonly TextBlock _connectionText;
    private readonly TextBlock _partyCountText;
    private readonly TextBlock _pokemonCountText;
    private readonly StackPanel _partyPanel;
    private readonly StackPanel _pokemonPanel;
    private readonly TextBlock _detailsTitle;
    private readonly TextBlock _detailsText;
    private readonly DispatcherTimer _refreshTimer;

    private SoulBuddyRuntime? _runtime;
    private bool _refreshInProgress;

    public MainWindow()
    {
        Title = "SoulBuddy";
        Width = 1280;
        Height = 780;
        MinWidth = 980;
        MinHeight = 620;
        Background = new SolidColorBrush(Color.Parse("#0F172A"));

        _statusText = CreateText(
            "SoulBuddy wird gestartet …",
            14,
            FontWeight.Medium,
            "#CBD5E1");

        _connectionText = CreateText(
            "Offline",
            14,
            FontWeight.SemiBold,
            "#94A3B8");

        _partyCountText = CreateText(
            "0 / 6",
            13,
            FontWeight.Medium,
            "#94A3B8");

        _pokemonCountText = CreateText(
            "0 Pokémon",
            13,
            FontWeight.Medium,
            "#94A3B8");

        _partyPanel = new StackPanel
        {
            Spacing = 10
        };

        _pokemonPanel = new StackPanel
        {
            Spacing = 8
        };

        _detailsTitle = CreateText(
            "Kein Pokémon ausgewählt",
            24,
            FontWeight.Bold,
            "#F8FAFC");

        _detailsText = CreateText(
            "Wähle links oder in der Mitte ein Pokémon aus.",
            14,
            FontWeight.Normal,
            "#CBD5E1");

        _detailsText.TextWrapping = TextWrapping.Wrap;

        Content = BuildLayout();

        _refreshTimer = new DispatcherTimer
        {
            Interval = TimeSpan.FromSeconds(1)
        };
        _refreshTimer.Tick += OnRefreshTimerTick;

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
            ColumnDefinitions = new ColumnDefinitions("320,*,360"),
            Margin = new Thickness(20, 16, 20, 16),
            ColumnSpacing = 16
        };

        Grid.SetRow(contentGrid, 1);

        var partyCard = CreateCard(
            BuildSection(
                "Aktuelles Team",
                _partyCountText,
                _partyPanel));

        var pokemonCard = CreateCard(
            BuildSection(
                "Gespeicherte Pokémon",
                _pokemonCountText,
                _pokemonPanel));

        var detailsCard = CreateCard(BuildDetailsSection());

        contentGrid.Children.Add(partyCard);
        Grid.SetColumn(pokemonCard, 1);
        contentGrid.Children.Add(pokemonCard);
        Grid.SetColumn(detailsCard, 2);
        contentGrid.Children.Add(detailsCard);

        root.Children.Add(contentGrid);

        var footer = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#111C31")),
            BorderBrush = new SolidColorBrush(Color.Parse("#24324A")),
            BorderThickness = new Thickness(0, 1, 0, 0),
            Padding = new Thickness(20, 12)
        };

        var footerGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        footerGrid.Children.Add(_statusText);
        Grid.SetColumn(_connectionText, 1);
        footerGrid.Children.Add(_connectionText);
        footer.Child = footerGrid;

        Grid.SetRow(footer, 2);
        root.Children.Add(footer);

        return root;
    }

    private Control BuildHeader()
    {
        var header = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#111C31")),
            BorderBrush = new SolidColorBrush(Color.Parse("#24324A")),
            BorderThickness = new Thickness(0, 0, 0, 1),
            Padding = new Thickness(22, 16)
        };

        var grid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto")
        };

        var titlePanel = new StackPanel
        {
            Spacing = 2
        };

        titlePanel.Children.Add(CreateText(
            "SoulBuddy",
            28,
            FontWeight.Bold,
            "#F8FAFC"));

        titlePanel.Children.Add(CreateText(
            "Lokaler Nuzlocke- und SoulLink-Begleiter",
            13,
            FontWeight.Normal,
            "#94A3B8"));

        grid.Children.Add(titlePanel);

        var badge = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#172554")),
            CornerRadius = new CornerRadius(18),
            Padding = new Thickness(14, 8),
            VerticalAlignment = VerticalAlignment.Center
        };

        badge.Child = CreateText(
            "DESKTOP PREVIEW",
            12,
            FontWeight.Bold,
            "#93C5FD");

        Grid.SetColumn(badge, 1);
        grid.Children.Add(badge);
        header.Child = grid;

        return header;
    }

    private Control BuildSection(
        string title,
        TextBlock countText,
        StackPanel contentPanel)
    {
        var grid = new Grid
        {
            RowDefinitions = new RowDefinitions("Auto,*")
        };

        var header = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,Auto"),
            Margin = new Thickness(0, 0, 0, 14)
        };

        header.Children.Add(CreateText(
            title,
            19,
            FontWeight.Bold,
            "#F8FAFC"));

        Grid.SetColumn(countText, 1);
        header.Children.Add(countText);
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
        var panel = new StackPanel
        {
            Spacing = 16
        };

        panel.Children.Add(CreateText(
            "Pokémon-Details",
            19,
            FontWeight.Bold,
            "#F8FAFC"));

        var separator = new Border
        {
            Height = 1,
            Background = new SolidColorBrush(Color.Parse("#334155"))
        };

        panel.Children.Add(separator);
        panel.Children.Add(_detailsTitle);
        panel.Children.Add(_detailsText);

        return panel;
    }

    private static Border CreateCard(Control content)
    {
        return new Border
        {
            Background = new SolidColorBrush(Color.Parse("#172033")),
            BorderBrush = new SolidColorBrush(Color.Parse("#2A3952")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(14),
            Padding = new Thickness(18),
            Child = content
        };
    }

    private static TextBlock CreateText(
        string text,
        double fontSize,
        FontWeight fontWeight,
        string color)
    {
        return new TextBlock
        {
            Text = text,
            FontSize = fontSize,
            FontWeight = fontWeight,
            Foreground = new SolidColorBrush(Color.Parse(color))
        };
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        try
        {
            _runtime = await SoulBuddyRuntime.CreateAsync();
            _runtime.Start();

            _connectionText.Text = _runtime.Config.SoullockeEnabled
                ? "Soullocke aktiviert"
                : "Lokal / Offline";

            _connectionText.Foreground = new SolidColorBrush(
                Color.Parse(
                    _runtime.Config.SoullockeEnabled
                        ? "#86EFAC"
                        : "#93C5FD"));

            _statusText.Text =
                $"Collector aktiv · {_runtime.EventFilePath}";

            await RefreshAsync();
            _refreshTimer.Start();
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Startfehler: {ex.Message}";
            _statusText.Foreground = new SolidColorBrush(
                Color.Parse("#FCA5A5"));
        }
    }

    private async void OnRefreshTimerTick(object? sender, EventArgs eventArgs)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_runtime is null || _refreshInProgress)
        {
            return;
        }

        _refreshInProgress = true;

        try
        {
            var party = await _runtime.LivePartySource.ReadPartyAsync(
                CancellationToken.None);

            var pokemon = await _runtime.KnownPokemonStore.GetAllAsync(
                CancellationToken.None);

            RenderParty(party);
            RenderPokemon(pokemon);

            _statusText.Text =
                $"Collector aktiv · Letzte Aktualisierung {DateTime.Now:HH:mm:ss}";
        }
        catch (Exception ex)
        {
            _statusText.Text = $"Aktualisierungsfehler: {ex.Message}";
        }
        finally
        {
            _refreshInProgress = false;
        }
    }

    private void RenderParty(IReadOnlyList<PartySlot> party)
    {
        _partyPanel.Children.Clear();

        var occupiedSlots = party
            .Where(slot => slot.Pokemon is not null)
            .OrderBy(slot => slot.SlotId)
            .ToList();

        _partyCountText.Text = $"{occupiedSlots.Count} / 6";

        if (occupiedSlots.Count == 0)
        {
            _partyPanel.Children.Add(CreateEmptyState(
                "Warte auf Teamdaten vom Emulator …"));
            return;
        }

        foreach (var slot in occupiedSlots)
        {
            var pokemon = slot.Pokemon!;
            var button = CreatePokemonButton(
                pokemon.Nickname,
                pokemon.SpeciesName,
                pokemon.Level,
                $"KP {pokemon.Hp.Current}/{pokemon.Hp.Max}",
                () => ShowPartyPokemonDetails(pokemon));

            _partyPanel.Children.Add(button);
        }
    }

    private void RenderPokemon(IReadOnlyList<KnownPokemonEntry> pokemon)
    {
        _pokemonPanel.Children.Clear();
        _pokemonCountText.Text = $"{pokemon.Count} Pokémon";

        if (pokemon.Count == 0)
        {
            _pokemonPanel.Children.Add(CreateEmptyState(
                "Noch keine Pokémon lokal gespeichert."));
            return;
        }

        foreach (var entry in pokemon.OrderByDescending(item => item.FirstSeenAt))
        {
            var syncStatus = entry.SoullockeSynced
                ? "Soullocke synchronisiert"
                : entry.Location;

            var button = CreatePokemonButton(
                entry.Nickname,
                entry.Species,
                entry.CurrentLevel,
                syncStatus,
                () => ShowStoredPokemonDetails(entry));

            _pokemonPanel.Children.Add(button);
        }
    }

    private static Control CreateEmptyState(string text)
    {
        var border = new Border
        {
            Background = new SolidColorBrush(Color.Parse("#111827")),
            CornerRadius = new CornerRadius(10),
            Padding = new Thickness(14)
        };

        var label = CreateText(
            text,
            13,
            FontWeight.Normal,
            "#94A3B8");

        label.TextWrapping = TextWrapping.Wrap;
        border.Child = label;
        return border;
    }

    private static Button CreatePokemonButton(
        string? nickname,
        string species,
        int level,
        string subtitle,
        Action onClick)
    {
        var displayName = string.IsNullOrWhiteSpace(nickname)
            ? species
            : nickname;

        var speciesSuffix = string.Equals(
            displayName,
            species,
            StringComparison.OrdinalIgnoreCase)
            ? string.Empty
            : $" · {species}";

        var textPanel = new StackPanel
        {
            Spacing = 3
        };

        textPanel.Children.Add(CreateText(
            $"{displayName}{speciesSuffix}",
            15,
            FontWeight.SemiBold,
            "#F8FAFC"));

        textPanel.Children.Add(CreateText(
            $"Level {level} · {subtitle}",
            12,
            FontWeight.Normal,
            "#94A3B8"));

        var button = new Button
        {
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            Padding = new Thickness(13, 11),
            Background = new SolidColorBrush(Color.Parse("#111827")),
            BorderBrush = new SolidColorBrush(Color.Parse("#334155")),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(10),
            Content = textPanel
        };

        button.Click += (_, _) => onClick();
        return button;
    }

    private void ShowPartyPokemonDetails(PartyPokemon pokemon)
    {
        _detailsTitle.Text = string.IsNullOrWhiteSpace(pokemon.Nickname)
            ? pokemon.SpeciesName
            : pokemon.Nickname;

        _detailsText.Text =
            $"Spezies: {pokemon.SpeciesName} (#{pokemon.Species})\n" +
            $"Level: {pokemon.Level}\n" +
            $"KP: {pokemon.Hp.Current}/{pokemon.Hp.Max}\n" +
            $"Fanglevel: {pokemon.LevelMet}\n" +
            $"Fangort-ID: {pokemon.LocationMet}\n" +
            $"PID: {pokemon.Pid}\n" +
            $"Trainer-ID: {pokemon.OriginalTrainerId}\n" +
            $"Secret-ID: {pokemon.OriginalTrainerSecretId}";
    }

    private void ShowStoredPokemonDetails(KnownPokemonEntry pokemon)
    {
        _detailsTitle.Text = string.IsNullOrWhiteSpace(pokemon.Nickname)
            ? pokemon.Species
            : pokemon.Nickname;

        _detailsText.Text =
            $"Spezies: {pokemon.Species} (#{pokemon.SpeciesId})\n" +
            $"Level: {pokemon.CurrentLevel}\n" +
            $"KP: {pokemon.CurrentHp}/{pokemon.MaxHp}\n" +
            $"Fangort: {pokemon.Location}\n" +
            $"Fanglevel: {pokemon.LevelMet}\n" +
            $"PID: {pokemon.Pid}\n" +
            $"Erstmals erkannt: {pokemon.FirstSeenAt.LocalDateTime:g}\n" +
            $"Zuletzt gesehen: {pokemon.LastSeenAt.LocalDateTime:g}\n" +
            $"Soullocke: {(pokemon.SoullockeSynced ? "synchronisiert" : "ausstehend")}";
    }

    private async void OnClosing(
        object? sender,
        WindowClosingEventArgs eventArgs)
    {
        _refreshTimer.Stop();

        if (_runtime is not null)
        {
            await _runtime.DisposeAsync();
            _runtime = null;
        }
    }
}
