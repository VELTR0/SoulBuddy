using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
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
        AddInternetAddressField(partnerStack, buttonGrid);
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
        sessionGrid.Children.Remove(localCard);

        sessionStack.Children.Add(new Border
        {
            Height = 1,
            Background = Brush("#334155"),
            Margin = new Thickness(0, 7, 0, 5)
        });
        sessionStack.Children.Add(localStack);

        sessionGrid.ColumnDefinitions = new ColumnDefinitions("1.15*,1.85*");
        Grid.SetColumn(sessionCard, 0);
        Grid.SetColumn(partnerCard, 1);
    }

    private static void AddInternetAddressField(
        StackPanel partnerStack,
        Grid buttonGrid)
    {
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
            BorderThickness = new Thickness(1)
        };
        addressBox.TextChanged += (_, _) =>
        {
            if (SoulBuddyNetworkService.Current is { } service)
            {
                service.JoinAddress = addressBox.Text?.Trim() ?? string.Empty;
            }
        };

        var buttonIndex = partnerStack.Children.IndexOf(buttonGrid);
        partnerStack.Children.Insert(buttonIndex, label);
        partnerStack.Children.Insert(buttonIndex + 1, addressBox);
    }

    private static SolidColorBrush Brush(string color) =>
        new(Color.Parse(color));
}
