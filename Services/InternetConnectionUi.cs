using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

internal static class InternetConnectionUi
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
        var joinButton = window
            .GetVisualDescendants()
            .OfType<Button>()
            .FirstOrDefault(button =>
                string.Equals(
                    button.Content?.ToString(),
                    "Beitreten",
                    StringComparison.Ordinal));

        if (joinButton is null)
        {
            return false;
        }

        var buttonGrid = joinButton
            .GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(grid => grid.Children.Contains(joinButton));
        var partnerStack = buttonGrid?
            .GetVisualAncestors()
            .OfType<StackPanel>()
            .FirstOrDefault(panel => panel.Children.Contains(buttonGrid));

        if (buttonGrid is null || partnerStack is null)
        {
            return false;
        }

        ArrangePartnerNetworkArea(partnerStack, buttonGrid);
        MoveAllStatusCardsIntoHeader(window, partnerStack);
        return true;
    }

    private static void ArrangePartnerNetworkArea(
        StackPanel partnerStack,
        Grid buttonGrid)
    {
        var statusControls = partnerStack.Children
            .Where(child => !ReferenceEquals(child, buttonGrid))
            .ToArray();

        foreach (var child in statusControls)
        {
            partnerStack.Children.Remove(child);
        }
        partnerStack.Children.Remove(buttonGrid);

        var statusPanel = new StackPanel
        {
            Spacing = 4,
            VerticalAlignment = VerticalAlignment.Top,
            MinWidth = 0
        };
        foreach (var child in statusControls)
        {
            statusPanel.Children.Add(child);
        }

        var label = new TextBlock
        {
            Text = "INTERNET-ADRESSE DES HOSTS (OPTIONAL)",
            FontSize = 8,
            FontWeight = FontWeight.Bold,
            Foreground = Brush("#93C5FD")
        };
        var addressBox = new TextBox
        {
            PlaceholderText = "z. B. 84.123.45.67:45831",
            FontSize = 10,
            MinHeight = 28,
            Padding = new Thickness(7, 4),
            Background = Brush("#0F1829"),
            Foreground = Brush("#F8FAFC"),
            BorderBrush = Brush("#334E8A"),
            BorderThickness = new Thickness(1),
            HorizontalAlignment = HorizontalAlignment.Stretch
        };
        addressBox.TextChanged += (_, _) =>
        {
            if (SoulBuddyNetworkService.Current is { } service)
            {
                service.JoinAddress = addressBox.Text?.Trim() ?? string.Empty;
            }
        };

        var controlsPanel = new StackPanel
        {
            Spacing = 5,
            VerticalAlignment = VerticalAlignment.Top,
            MinWidth = 0
        };
        controlsPanel.Children.Add(label);
        controlsPanel.Children.Add(addressBox);
        controlsPanel.Children.Add(buttonGrid);

        var separator = VerticalSeparator();
        var layout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("1*,Auto,1.35*"),
            ColumnSpacing = 10,
            MinWidth = 0
        };
        layout.Children.Add(statusPanel);
        Grid.SetColumn(separator, 1);
        layout.Children.Add(separator);
        Grid.SetColumn(controlsPanel, 2);
        layout.Children.Add(controlsPanel);

        partnerStack.Children.Add(layout);
    }

    private static void MoveAllStatusCardsIntoHeader(
        Window window,
        StackPanel partnerStack)
    {
        var partnerCard = partnerStack
            .GetVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border => ReferenceEquals(border.Child, partnerStack));
        var sessionGrid = partnerCard?
            .GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(grid =>
                grid.ColumnDefinitions.Count == 3 &&
                grid.Children.Contains(partnerCard));

        if (partnerCard is null || sessionGrid is null)
        {
            return;
        }

        var sessionCard = sessionGrid.Children
            .OfType<Border>()
            .FirstOrDefault(card => Grid.GetColumn(card) == 0);
        var localCard = sessionGrid.Children
            .OfType<Border>()
            .FirstOrDefault(card => Grid.GetColumn(card) == 1);

        if (sessionCard?.Child is not StackPanel sessionStack ||
            localCard?.Child is not StackPanel localStack)
        {
            return;
        }

        var phaseText = window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text =>
                string.Equals(text.Text, "PHASE 3C", StringComparison.Ordinal));
        var phaseBadge = phaseText?
            .GetVisualAncestors()
            .OfType<Border>()
            .FirstOrDefault(border => ReferenceEquals(border.Child, phaseText));
        var headerGrid = phaseBadge?
            .GetVisualAncestors()
            .OfType<Grid>()
            .FirstOrDefault(grid => grid.Children.Contains(phaseBadge));

        if (phaseBadge is null || headerGrid is null)
        {
            return;
        }

        // Detach the contents before reusing them in the combined header card.
        sessionCard.Child = null;
        localCard.Child = null;
        partnerCard.Child = null;

        // Remove the old cards from their original parent before reparenting.
        sessionGrid.Children.Remove(sessionCard);
        sessionGrid.Children.Remove(localCard);
        sessionGrid.Children.Remove(partnerCard);

        var sessionSeparator = VerticalSeparator();
        var playerSeparator = VerticalSeparator();
        var statusLayout = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("0.7*,Auto,0.95*,Auto,2.15*"),
            ColumnSpacing = 12,
            MinWidth = 0
        };

        sessionStack.VerticalAlignment = VerticalAlignment.Top;
        statusLayout.Children.Add(sessionStack);

        Grid.SetColumn(sessionSeparator, 1);
        statusLayout.Children.Add(sessionSeparator);

        localStack.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(localStack, 2);
        statusLayout.Children.Add(localStack);

        Grid.SetColumn(playerSeparator, 3);
        statusLayout.Children.Add(playerSeparator);

        partnerStack.VerticalAlignment = VerticalAlignment.Top;
        Grid.SetColumn(partnerStack, 4);
        statusLayout.Children.Add(partnerStack);

        sessionCard.Child = statusLayout;
        sessionCard.Margin = new Thickness(16, 0, 0, 0);
        sessionCard.VerticalAlignment = VerticalAlignment.Center;
        sessionCard.HorizontalAlignment = HorizontalAlignment.Stretch;

        headerGrid.Children.Remove(phaseBadge);
        headerGrid.ColumnDefinitions = new ColumnDefinitions("0.42*,1.58*");
        Grid.SetColumn(sessionCard, 1);
        headerGrid.Children.Add(sessionCard);

        sessionGrid.IsVisible = false;
        sessionGrid.Height = 0;
        sessionGrid.Margin = new Thickness(0);
    }

    private static Border VerticalSeparator() => new()
    {
        Width = 1,
        Background = Brush("#334155"),
        Margin = new Thickness(2, 0),
        VerticalAlignment = VerticalAlignment.Stretch
    };

    private static SolidColorBrush Brush(string color) =>
        new(Color.Parse(color));
}
