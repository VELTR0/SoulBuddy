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
                Interval = TimeSpan.FromMilliseconds(400)
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
            ApplyColumnWidths(window);
            HideStoredPokemonHp(window);
        }
    }

    private static void ApplyColumnWidths(Window window)
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

        if (contentGrid is null)
        {
            return;
        }

        // Die gespeicherte Liste erhält ungefähr die Hälfte ihrer bisherigen
        // Breite. Der frei werdende Platz geht vollständig an das Team.
        contentGrid.ColumnDefinitions =
            new ColumnDefinitions("2.15*,0.62*,1.1*");
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
                     .OfType<TextBlock>())
        {
            if (IsHpText(text.Text))
            {
                text.IsVisible = false;
            }
        }
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
                (grid.Children.OfType<Grid>().Any() ||
                 grid.Children.OfType<ScrollViewer>().Any()));
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
