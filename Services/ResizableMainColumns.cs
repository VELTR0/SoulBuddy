using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Templates;
using Avalonia.Input;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

/// <summary>
/// Adds draggable vertical splitters between the three main content cards:
/// team, stored Pokémon and live/details.
/// </summary>
internal static class ResizableMainColumns
{
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
            _discoveryTimer.Tick += (_, _) => AttachToOpenWindows();
            _discoveryTimer.Start();
            AttachToOpenWindows();
        });
    }

    private static void AttachToOpenWindows()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (var window in desktop.Windows)
        {
            if (AttachedWindows.Contains(window) || !TryAttach(window))
            {
                continue;
            }

            AttachedWindows.Add(window);
            window.Closed += (_, _) => AttachedWindows.Remove(window);
        }
    }

    private static bool TryAttach(Window window)
    {
        var teamHeader = window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text => string.Equals(
                text.Text,
                "Aktuelles Team",
                StringComparison.Ordinal));

        var teamCard = teamHeader?
            .GetVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border => border.Child is Grid);

        var contentGrid = teamCard?
            .GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(grid =>
                grid.Children.OfType<Border>().Count() >= 3 &&
                grid.ColumnDefinitions.Count == 3);

        if (contentGrid is null)
        {
            return false;
        }

        var cards = contentGrid.Children
            .OfType<Border>()
            .OrderBy(Grid.GetColumn)
            .Take(3)
            .ToArray();

        if (cards.Length != 3)
        {
            return false;
        }

        cards[0].MinWidth = 300;
        cards[1].MinWidth = 180;
        cards[2].MinWidth = 260;

        contentGrid.ColumnDefinitions = new ColumnDefinitions(
            "2.15*,8,0.62*,8,1.1*");
        contentGrid.ColumnSpacing = 0;

        Grid.SetColumn(cards[0], 0);
        Grid.SetColumn(cards[1], 2);
        Grid.SetColumn(cards[2], 4);

        var firstSplitter = CreateSplitter();
        Grid.SetColumn(firstSplitter, 1);
        contentGrid.Children.Add(firstSplitter);

        var secondSplitter = CreateSplitter();
        Grid.SetColumn(secondSplitter, 3);
        contentGrid.Children.Add(secondSplitter);

        return true;
    }

    private static GridSplitter CreateSplitter() => new()
    {
        Width = 8,
        HorizontalAlignment = HorizontalAlignment.Stretch,
        VerticalAlignment = VerticalAlignment.Stretch,
        ResizeDirection = GridResizeDirection.Columns,
        ResizeBehavior = GridResizeBehavior.PreviousAndNext,
        Background = Brushes.Transparent,
        Cursor = new Cursor(StandardCursorType.SizeWestEast),
        Margin = new Thickness(1, 8),
        Template = new FuncControlTemplate<GridSplitter>((_, _) =>
            new Border
            {
                Width = 4,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Stretch,
                Background = new SolidColorBrush(Color.Parse("#2B3C58")),
                CornerRadius = new CornerRadius(2)
            })
    };
}
