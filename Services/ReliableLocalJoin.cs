using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

/// <summary>
/// Repeats local discovery while a join attempt is running. The network
/// service currently sends one UDP discovery packet per attempt, while host
/// startup can be delayed by the optional UPnP check. Repeating the attempt
/// makes the normal flow reliable: one player hosts, the other joins.
/// </summary>
internal static class ReliableLocalJoin
{
    private static readonly HashSet<Button> AttachedButtons = [];
    private static readonly Dictionary<Button, JoinRetryState> RetryStates = [];
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
            _discoveryTimer.Tick += (_, _) => DiscoverJoinButtons();
            _discoveryTimer.Start();
            DiscoverJoinButtons();
        });
    }

    private static void DiscoverJoinButtons()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (var window in desktop.Windows)
        {
            foreach (var button in window
                         .GetVisualDescendants()
                         .OfType<Button>()
                         .Where(candidate => string.Equals(
                             candidate.Content?.ToString(),
                             "Beitreten",
                             StringComparison.Ordinal)))
            {
                if (!AttachedButtons.Add(button))
                {
                    continue;
                }

                button.Click += (_, _) =>
                    Dispatcher.UIThread.Post(() => BeginRetry(button));
                window.Closed += (_, _) => StopRetry(button);
            }
        }
    }

    private static void BeginRetry(Button button)
    {
        StopRetry(button);

        var service = SoulBuddyNetworkService.Current;
        if (service is null ||
            service.Mode != SoulBuddyNetworkMode.Join ||
            !string.IsNullOrWhiteSpace(service.JoinAddress) ||
            string.IsNullOrWhiteSpace(service.SessionId) ||
            string.IsNullOrWhiteSpace(service.PlayerName))
        {
            return;
        }

        var state = new JoinRetryState(
            service.SessionId,
            service.PlayerName,
            DateTimeOffset.UtcNow);
        var timer = new DispatcherTimer
        {
            Interval = TimeSpan.FromMilliseconds(1500)
        };
        state.Timer = timer;
        RetryStates[button] = state;

        timer.Tick += (_, _) => RetryJoin(button, state);
        timer.Start();
    }

    private static void RetryJoin(Button button, JoinRetryState state)
    {
        var service = SoulBuddyNetworkService.Current;
        if (service is null ||
            service.State == SoulBuddyNetworkState.Connected ||
            service.Mode != SoulBuddyNetworkMode.Join ||
            DateTimeOffset.UtcNow - state.StartedAt > TimeSpan.FromSeconds(12))
        {
            StopRetry(button);
            return;
        }

        // Internet joins already have their own direct connection attempt.
        // This retry is deliberately limited to automatic LAN discovery.
        if (!string.IsNullOrWhiteSpace(service.JoinAddress))
        {
            StopRetry(button);
            return;
        }

        try
        {
            service.PrepareJoin(state.SessionId, state.PlayerName, null);
        }
        catch
        {
            // The next timer tick retries. The service keeps the user-facing
            // error status, so failures remain visible instead of crashing UI.
        }
    }

    private static void StopRetry(Button button)
    {
        if (!RetryStates.Remove(button, out var state))
        {
            return;
        }

        state.Timer?.Stop();
    }

    private sealed class JoinRetryState(
        string sessionId,
        string playerName,
        DateTimeOffset startedAt)
    {
        public string SessionId { get; } = sessionId;
        public string PlayerName { get; } = playerName;
        public DateTimeOffset StartedAt { get; } = startedAt;
        public DispatcherTimer? Timer { get; set; }
    }
}
