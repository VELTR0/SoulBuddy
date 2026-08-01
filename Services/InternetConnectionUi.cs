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

        MergeSessionAndLocalPlayerCards(partnerStack);
        ArrangePartnerNetworkArea(partnerStack, buttonGrid);
        MoveNetworkCardIntoHeader(window, partnerStack);
        return true;
    }

    private static void MergeSessionAndLocalPlayerCards(StackPanel partnerStack)
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

        localCard.Child = null;
        sessionCard.Child = null;
        sessionGrid.Children.Remove(localCard);

        sessionStack.Children.Add(new Border
        {
            Height = 1,
            Background = Brush("#334155"),
            Margin = new Thickness(0, 8, 0, 6)
        });

        foreach (var child in localStack.Children.ToArray())
        {
            localStack.Children.Remove(child);
            sessionStack.Children.Add(child);
        }

        sessionCard.Child = sessionStack;
        sessionGrid.ColumnDefinitions = new ColumnDefinitions("1.15*,1.85*");
        Grid.SetColumn(sessionCard, 0);
        Grid.SetColumn(partnerCard, 1);
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

        var separator = new Border
        {
            Width = 1,
            Background = Brush("#334155"),
            Margin = new Thickness(4, 0),
            VerticalAlignment = VerticalAlignment.Stretch
        };

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

    private static void MoveNetworkCardIntoHeader(
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
            .FirstOrDefault(grid => grid.Children.Contains(partnerCard));

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

        if (partnerCard is null || sessionGrid is null ||
            phaseBadge is null || headerGrid is null)
        {
            return;
        }

        var sessionCard = sessionGrid.Children
            .OfType<Border>()
            .FirstOrDefault(card => !ReferenceEquals(card, partnerCard));

        sessionGrid.Children.Remove(partnerCard);
        sessionGrid.ColumnDefinitions = new ColumnDefinitions("*");
        if (sessionCard is not null)
        {
            Grid.SetColumn(sessionCard, 0);
        }

        headerGrid.Children.Remove(phaseBadge);
        headerGrid.ColumnDefinitions = new ColumnDefinitions("0.55*,1.45*");

        partnerCard.Margin = new Thickness(16, 0, 0, 0);
        partnerCard.VerticalAlignment = VerticalAlignment.Center;
        Grid.SetColumn(partnerCard, 1);
        headerGrid.Children.Add(partnerCard);
    }

    private static SolidColorBrush Brush(string color) =>
        new(Color.Parse(color));
}
