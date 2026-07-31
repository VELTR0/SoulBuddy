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
        });
    }

    private static void UpdateOpenWindows()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (var window in desktop.Windows)
        {
            UpdateMainContentColumns(window);
            UpdatePartyGrid(window);
            StackPartyHpBelowLevel(window);
            HideStoredPokemonHp(window);
        }
    }

    private static void UpdateMainContentColumns(Window window)
    {
        var storedHeader = FindHeader(window, "Gespeicherte Pokémon");
        var storedSection = FindSectionGrid(storedHeader);
        var storedCard = storedSection?
            .GetVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault();

        var contentGrid = storedCard?
            .GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(grid => grid.ColumnDefinitions.Count == 3);

        if (contentGrid is null)
        {
            return;
        }

        // Die gespeicherte Liste bleibt kompakt, erhält aber etwas mehr Platz
        // für Namen und Fangorte als in der vorherigen Fassung.
        contentGrid.ColumnDefinitions =
            new ColumnDefinitions("2.05*,0.78*,1.1*");
    }

    private static void UpdatePartyGrid(Window window)
    {
        var partyHeader = FindHeader(window, "Aktuelles Team");
        var partySection = FindSectionGrid(partyHeader);
        var partyGrid = partySection?.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => Grid.GetRow(grid) == 1);

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
        var partyHeader = FindHeader(window, "Aktuelles Team");
        var partySection = FindSectionGrid(partyHeader);
        var partyGrid = partySection?.Children
            .OfType<Grid>()
            .FirstOrDefault(grid => Grid.GetRow(grid) == 1);

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

            if (hpGrid is null || hpGrid.Children.Count < 3)
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
        var storedHeader = FindHeader(window, "Gespeicherte Pokémon");
        var storedSection = FindSectionGrid(storedHeader);
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
                     .OfType<TextBlock>())
        {
            if (IsHpText(text.Text))
            {
                text.IsVisible = false;
            }
        }
    }

    private static TextBlock? FindHeader(Window window, string title) =>
        window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text =>
                string.Equals(text.Text, title, StringComparison.Ordinal));

    private static Grid? FindSectionGrid(TextBlock? header)
    {
        if (header is null)
        {
            return null;
        }

        return header
            .GetVisualAncestors()
            .OfType<Grid>()
            .Skip(1)
            .FirstOrDefault();
    }

    private static bool IsHpText(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) ||
            !value.EndsWith(" KP", StringComparison.Ordinal))
        {
            return false;
        }

        var slashIndex = value.IndexOf('/');
        return slashIndex > 0;
    }
}
