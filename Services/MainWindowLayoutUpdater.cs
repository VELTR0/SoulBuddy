using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

internal static class MainWindowLayoutUpdater
{
    private static readonly HashSet<Window> AttachedWindows = [];
    private static readonly HashSet<Window> UpdatingWindows = [];
    private static DispatcherTimer? _windowDiscoveryTimer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _windowDiscoveryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _windowDiscoveryTimer.Tick += (_, _) => AttachOpenWindows();
            _windowDiscoveryTimer.Start();
            AttachOpenWindows();
        });
    }

    private static void AttachOpenWindows()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (var window in desktop.Windows)
        {
            if (!AttachedWindows.Add(window))
            {
                continue;
            }

            window.LayoutUpdated += OnWindowLayoutUpdated;
            window.Closed += (_, _) =>
            {
                window.LayoutUpdated -= OnWindowLayoutUpdated;
                AttachedWindows.Remove(window);
                UpdatingWindows.Remove(window);
            };

            UpdateWindow(window);
        }
    }

    private static void OnWindowLayoutUpdated(object? sender, EventArgs eventArgs)
    {
        if (sender is Window window)
        {
            UpdateWindow(window);
        }
    }

    private static void UpdateWindow(Window window)
    {
        if (!UpdatingWindows.Add(window))
        {
            return;
        }

        try
        {
            UpdateMainContentColumns(window);
            UpdatePartyGrid(window);
            StackPartyHpBelowLevel(window);
            HideStoredPokemonHp(window);
        }
        finally
        {
            UpdatingWindows.Remove(window);
        }
    }

    private static void UpdateMainContentColumns(Window window)
    {
        var storedSection = FindSection(window, "Gespeicherte Pokémon");
        var storedCard = storedSection?
            .GetVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault();

        var contentGrid = storedCard?
            .GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(grid => grid.ColumnDefinitions.Count == 3);

        if (contentGrid is not null)
        {
            contentGrid.ColumnDefinitions =
                new ColumnDefinitions("2.05*,0.78*,1.1*");
        }
    }

    private static void UpdatePartyGrid(Window window)
    {
        var partyGrid = FindPartyGrid(window);
        if (partyGrid is null)
        {
            return;
        }

        partyGrid.RowDefinitions = new RowDefinitions("*,*,*");
        partyGrid.ColumnDefinitions = new ColumnDefinitions("*,*");
        partyGrid.RowSpacing = 5;
        partyGrid.ColumnSpacing = 5;

        var cards = partyGrid.Children.ToArray();
        for (var index = 0; index < cards.Length && index < 6; index++)
        {
            Grid.SetRow(cards[index], index / 2);
            Grid.SetColumn(cards[index], index % 2);
        }
    }

    private static void StackPartyHpBelowLevel(Window window)
    {
        var partyGrid = FindPartyGrid(window);
        if (partyGrid is null)
        {
            return;
        }

        foreach (var progressBar in partyGrid
                     .GetVisualDescendants()
                     .OfType<ProgressBar>())
        {
            var hpGrid = progressBar
                .GetVisualAncestors()
                .OfType<Grid>()
                .FirstOrDefault(grid => grid.Children.Contains(progressBar));

            if (hpGrid is null)
            {
                continue;
            }

            var levelText = hpGrid.Children
                .OfType<TextBlock>()
                .FirstOrDefault(text =>
                    text.Text?.StartsWith("Level ", StringComparison.Ordinal) == true);
            var hpText = hpGrid.Children
                .OfType<TextBlock>()
                .FirstOrDefault(text => IsHpText(text.Text));

            if (levelText is null || hpText is null)
            {
                continue;
            }

            // Team-KP werden niemals ausgeblendet.
            progressBar.IsVisible = true;
            hpText.IsVisible = true;

            hpGrid.ColumnDefinitions = new ColumnDefinitions("*");
            hpGrid.RowDefinitions = new RowDefinitions("Auto,Auto,Auto");
            hpGrid.ColumnSpacing = 0;
            hpGrid.RowSpacing = 2;

            Grid.SetColumn(levelText, 0);
            Grid.SetRow(levelText, 0);

            Grid.SetColumn(progressBar, 0);
            Grid.SetRow(progressBar, 1);
            progressBar.HorizontalAlignment = HorizontalAlignment.Stretch;
            progressBar.MinWidth = 0;

            Grid.SetColumn(hpText, 0);
            Grid.SetRow(hpText, 2);
            hpText.HorizontalAlignment = HorizontalAlignment.Left;
        }
    }

    private static void HideStoredPokemonHp(Window window)
    {
        var storedSection = FindSection(window, "Gespeicherte Pokémon");
        var scrollViewer = storedSection?.Children
            .OfType<ScrollViewer>()
            .FirstOrDefault(viewer => Grid.GetRow(viewer) == 1);

        if (scrollViewer is null)
        {
            return;
        }

        foreach (var progressBar in scrollViewer
                     .GetVisualDescendants()
                     .OfType<ProgressBar>())
        {
            progressBar.IsVisible = false;
        }

        foreach (var text in scrollViewer
                     .GetVisualDescendants()
                     .OfType<TextBlock>()
                     .Where(text => IsHpText(text.Text)))
        {
            text.IsVisible = false;
        }
    }

    private static Grid? FindPartyGrid(Window window)
    {
        var partySection = FindSection(window, "Aktuelles Team");
        return partySection?.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => Grid.GetRow(grid) == 1);
    }

    private static Grid? FindSection(Window window, string title)
    {
        var header = window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text =>
                string.Equals(text.Text, title, StringComparison.Ordinal));

        if (header is null)
        {
            return null;
        }

        return header
            .GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(grid =>
                grid.RowDefinitions.Count == 2 &&
                grid.Children.OfType<Grid>().Any() ||
                grid.Children.OfType<ScrollViewer>().Any());
    }

    private static bool IsHpText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.EndsWith(" KP", StringComparison.Ordinal))
        {
            return false;
        }

        return value.IndexOf('/') > 0;
    }
}
