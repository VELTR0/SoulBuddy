using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

internal static class MainWindowLayoutUpdater
{
    private static readonly HashSet<Panel> AttachedStoredPanels = [];
    private static readonly HashSet<Window> AttachedWindows = [];
    private static DispatcherTimer? _discoveryTimer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _discoveryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(500)
            };
            _discoveryTimer.Tick += (_, _) => DiscoverWindows();
            _discoveryTimer.Start();
            DiscoverWindows();
        });
    }

    private static void DiscoverWindows()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (var window in desktop.Windows)
        {
            ApplyColumnWidths(window);
            AttachStoredPanel(window);

            if (AttachedWindows.Add(window))
            {
                window.Closed += (_, _) => DetachWindow(window);
            }
        }
    }

    private static void AttachStoredPanel(Window window)
    {
        var storedSection = FindSection(window, "Gespeicherte Pokémon");
        var scrollViewer = storedSection?.Children
            .OfType<ScrollViewer>()
            .FirstOrDefault(viewer => Grid.GetRow(viewer) == 1);

        if (scrollViewer?.Content is not Panel storedPanel ||
            !AttachedStoredPanels.Add(storedPanel))
        {
            return;
        }

        storedPanel.Children.CollectionChanged += OnStoredChildrenChanged;
        Dispatcher.UIThread.Post(() => RemoveStoredHpControls(storedPanel));
    }

    private static void OnStoredChildrenChanged(
        object? sender,
        NotifyCollectionChangedEventArgs eventArgs)
    {
        if (sender is not Avalonia.Controls.Controls children)
        {
            return;
        }

        var panel = AttachedStoredPanels
            .FirstOrDefault(candidate => ReferenceEquals(candidate.Children, children));

        if (panel is not null)
        {
            Dispatcher.UIThread.Post(() => RemoveStoredHpControls(panel));
        }
    }

    private static void RemoveStoredHpControls(Panel storedPanel)
    {
        var progressBars = storedPanel
            .GetVisualDescendants()
            .OfType<ProgressBar>()
            .ToArray();

        foreach (var progressBar in progressBars)
        {
            var hpGrid = progressBar
                .GetVisualAncestors()
                .OfType<Grid>()
                .FirstOrDefault(grid => grid.Children.Contains(progressBar));

            if (hpGrid is null)
            {
                continue;
            }

            var hpText = hpGrid.Children
                .OfType<TextBlock>()
                .FirstOrDefault(text => IsHpText(text.Text));
            var levelText = hpGrid.Children
                .OfType<TextBlock>()
                .FirstOrDefault(text =>
                    text.Text?.StartsWith("Level ", StringComparison.Ordinal) == true);

            hpGrid.Children.Remove(progressBar);
            if (hpText is not null)
            {
                hpGrid.Children.Remove(hpText);
            }

            hpGrid.ColumnDefinitions = new ColumnDefinitions("*");
            hpGrid.RowDefinitions = new RowDefinitions("Auto");
            hpGrid.ColumnSpacing = 0;

            if (levelText is not null)
            {
                Grid.SetColumn(levelText, 0);
                Grid.SetRow(levelText, 0);
            }
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

        if (contentGrid is not null)
        {
            contentGrid.ColumnDefinitions =
                new ColumnDefinitions("2.15*,0.62*,1.1*");
        }
    }

    private static void DetachWindow(Window window)
    {
        AttachedWindows.Remove(window);

        var panels = AttachedStoredPanels
            .Where(panel => panel.GetVisualAncestors().Contains(window))
            .ToArray();

        foreach (var panel in panels)
        {
            panel.Children.CollectionChanged -= OnStoredChildrenChanged;
            AttachedStoredPanels.Remove(panel);
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
