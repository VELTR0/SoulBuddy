using System.Collections.Specialized;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

/// <summary>
/// Keeps the six party slots arranged like the in-game party screen:
/// two columns with three Pokémon each.
/// </summary>
internal static class MainWindowLayoutUpdater
{
    private static readonly Dictionary<Panel, NotifyCollectionChangedEventHandler>
        PartyPanelHandlers = [];
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
            AttachPartyPanel(window);

            if (AttachedWindows.Add(window))
            {
                window.Closed += (_, _) => DetachWindow(window);
            }
        }
    }

    private static void AttachPartyPanel(Window window)
    {
        var partyPanel = FindPartyPanel(window);
        if (partyPanel is null)
        {
            return;
        }

        ArrangeParty(partyPanel);

        if (PartyPanelHandlers.ContainsKey(partyPanel))
        {
            return;
        }

        NotifyCollectionChangedEventHandler handler = (_, _) =>
            Dispatcher.UIThread.Post(() => ArrangeParty(partyPanel));

        PartyPanelHandlers.Add(partyPanel, handler);
        partyPanel.Children.CollectionChanged += handler;
    }

    private static void ArrangeParty(Panel partyPanel)
    {
        if (partyPanel is not Grid grid)
        {
            return;
        }

        grid.RowDefinitions = new RowDefinitions("*,*,*");
        grid.ColumnDefinitions = new ColumnDefinitions("*,*");
        grid.RowSpacing = 5;
        grid.ColumnSpacing = 5;

        var cards = grid.Children.ToArray();
        for (var slot = 0; slot < cards.Length && slot < 6; slot++)
        {
            Grid.SetRow(cards[slot], slot / 2);
            Grid.SetColumn(cards[slot], slot % 2);
        }
    }

    private static Panel? FindPartyPanel(Window window)
    {
        var header = window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text =>
                string.Equals(
                    text.Text,
                    "Aktuelles Team",
                    StringComparison.Ordinal));

        if (header is null)
        {
            return null;
        }

        var section = header
            .GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(grid =>
                grid.RowDefinitions.Count == 2 &&
                grid.Children.OfType<Grid>().Any(child =>
                    Grid.GetRow(child) == 1));

        return section?.Children
            .OfType<Grid>()
            .FirstOrDefault(child => Grid.GetRow(child) == 1);
    }

    private static void DetachWindow(Window window)
    {
        AttachedWindows.Remove(window);

        var panels = PartyPanelHandlers.Keys
            .Where(panel => panel.GetVisualAncestors().Contains(window))
            .ToArray();

        foreach (var panel in panels)
        {
            var handler = PartyPanelHandlers[panel];
            panel.Children.CollectionChanged -= handler;
            PartyPanelHandlers.Remove(panel);
        }
    }
}
