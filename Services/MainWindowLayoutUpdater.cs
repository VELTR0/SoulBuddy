using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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

        // Das Team benötigt durch das 2x3-Raster mehr Platz. Die Box-Liste
        // erhält ungefähr die Hälfte ihrer bisherigen Breite.
        contentGrid.ColumnDefinitions =
            new ColumnDefinitions("2.2*,0.62*,1.1*");
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
