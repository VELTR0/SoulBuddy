using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SoulBuddy.Models;
using SoulBuddy.Views;

namespace SoulBuddy.Services;

/// <summary>
/// Starts SoulBuddy networking automatically. Every client first searches the
/// local network. If no peer is available, one client takes over hosting after
/// a short randomized delay while the other keeps searching.
/// </summary>
internal static class AutomaticSessionNetworking
{
    private const string LanDiscoveryChannel = "soulbuddy-lan";

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
        await Task.Delay(350);

        if (ContextField?.GetValue(window) is not SessionContext context ||
            NetworkField?.GetValue(window) is not SoulBuddyNetworkService network)
        {
            return;
        }

        var playerName = context.LocalPlayer.DisplayName;

        try
        {
            network.PrepareJoin(LanDiscoveryChannel, playerName, network.JoinAddress);

            var searchDuration = TimeSpan.FromMilliseconds(
                1800 + Random.Shared.Next(0, 2200));
            var searchUntil = DateTimeOffset.UtcNow + searchDuration;

            while (DateTimeOffset.UtcNow < searchUntil)
            {
                await Task.Delay(200);
                if (network.State == SoulBuddyNetworkState.Connected)
                {
                    return;
                }
            }

            if (network.State != SoulBuddyNetworkState.Connected)
            {
                network.PrepareHost(LanDiscoveryChannel, playerName);
            }
        }
        catch
        {
            // If another client won the host race, resume discovery instead of
            // surfacing a transient port-conflict to the user.
            await Task.Delay(Random.Shared.Next(350, 900));
            if (network.State != SoulBuddyNetworkState.Connected)
            {
                network.PrepareJoin(LanDiscoveryChannel, playerName, network.JoinAddress);
            }
        }
    }
}
