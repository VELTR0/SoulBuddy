using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoulBuddy.Models;
using SoulBuddy.Views;

namespace SoulBuddy.Services;

/// <summary>
/// Starts the network mode selected on the session screen automatically.
/// Creating a session hosts, joining a session searches for that host, and
/// continuing first searches before falling back to hosting locally.
/// </summary>
internal static class AutomaticSessionNetworking
{
    private static readonly FieldInfo? ContextField = typeof(MainWindow).GetField(
        "_sessionContext",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly FieldInfo? NetworkField = typeof(MainWindow).GetField(
        "_networkService",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly HashSet<MainWindow> StartedWindows = [];
    private static DispatcherTimer? _timer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(250)
            };
            _timer.Tick += (_, _) => DiscoverMainWindows();
            _timer.Start();
            DiscoverMainWindows();
        });
    }

    private static void DiscoverMainWindows()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (var window in desktop.Windows.OfType<MainWindow>())
        {
            HideManualNetworkButtons(window);

            if (!StartedWindows.Add(window))
            {
                continue;
            }

            window.Closed += (_, _) => StartedWindows.Remove(window);
            _ = StartForWindowAsync(window);
        }
    }

    private static async Task StartForWindowAsync(MainWindow window)
    {
        // Give the visual tree and the optional internet-address field time to
        // finish their setup before networking starts.
        await Task.Delay(350);

        if (ContextField?.GetValue(window) is not SessionContext context ||
            NetworkField?.GetValue(window) is not SoulBuddyNetworkService network)
        {
            return;
        }

        var sessionId = context.Session.Id;
        var playerName = context.LocalPlayer.DisplayName;

        try
        {
            switch (context.LaunchMode)
            {
                case SessionLaunchMode.Host:
                    network.PrepareHost(sessionId, playerName);
                    break;

                case SessionLaunchMode.Join:
                    network.PrepareJoin(sessionId, playerName, network.JoinAddress);
                    break;

                case SessionLaunchMode.Continue:
                    await ContinueAutomaticallyAsync(
                        network,
                        sessionId,
                        playerName,
                        window);
                    break;
            }
        }
        catch (Exception ex)
        {
            SetNetworkStatus(window, $"Automatischer Netzwerkstart fehlgeschlagen: {ex.Message}");
        }
    }

    private static async Task ContinueAutomaticallyAsync(
        SoulBuddyNetworkService network,
        string sessionId,
        string playerName,
        Window window)
    {
        network.PrepareJoin(sessionId, playerName, network.JoinAddress);
        SetNetworkStatus(window, "Suche automatisch nach einem bestehenden Host …");

        // A continued session does not know whether this client was the host.
        // Search first; if nobody is reachable, this client becomes the host.
        for (var attempt = 0; attempt < 24; attempt++)
        {
            await Task.Delay(250);

            if (network.State == SoulBuddyNetworkState.Connected)
            {
                return;
            }
        }

        if (network.State != SoulBuddyNetworkState.Connected)
        {
            SetNetworkStatus(window, "Kein Host gefunden · SoulBuddy übernimmt das Hosting …");
            network.PrepareHost(sessionId, playerName);
        }
    }

    private static void HideManualNetworkButtons(MainWindow window)
    {
        foreach (var button in window.GetVisualDescendants().OfType<Button>())
        {
            if (button.Content is string label &&
                (string.Equals(label, "Host", StringComparison.Ordinal) ||
                 string.Equals(label, "Beitreten", StringComparison.Ordinal)))
            {
                button.IsVisible = false;
                button.IsEnabled = false;
                button.Width = 0;
                button.Height = 0;
                button.Margin = new Thickness(0);
                button.Padding = new Thickness(0);
            }
        }
    }

    private static void SetNetworkStatus(Window window, string message)
    {
        var status = window
            .GetVisualDescendants()
            .OfType<TextBlock>()
            .FirstOrDefault(text =>
                text.Text is not null &&
                (text.Text.Contains("Netzwerk", StringComparison.OrdinalIgnoreCase) ||
                 text.Text.Contains("Host", StringComparison.OrdinalIgnoreCase) ||
                 text.Text.Contains("Session", StringComparison.OrdinalIgnoreCase)));

        if (status is not null)
        {
            status.Text = message;
        }
    }
}
