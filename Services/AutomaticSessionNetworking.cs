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
/// Starts SoulBuddy networking automatically. Every LAN client first searches
/// for a peer and one takes over hosting when needed. In Soullocke mode this
/// service deliberately remains inactive.
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
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => DiscoverMainWindows();
            _timer.Start();
            DiscoverMainWindows();
        });
    }

    private static void DiscoverMainWindows()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        foreach (var window in desktop.Windows.OfType<MainWindow>())
        {
            RemoveLegacySessionUi(window);
            RenameEncounterSection(window);

            if (!StartedWindows.Add(window)) continue;
            window.Closed += (_, _) => StartedWindows.Remove(window);
            if (!SoullockeLaunchSettings.Enabled) _ = StartForWindowAsync(window);
        }
    }

    private static void RenameEncounterSection(MainWindow window)
    {
        var heading = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text =>
            string.Equals(text.Text, "Gefangene Pokémon", StringComparison.Ordinal) ||
            string.Equals(text.Text, "Gespeicherte Pokémon", StringComparison.Ordinal));
        if (heading is not null) heading.Text = "Begegnungen";
    }

    private static void RemoveLegacySessionUi(MainWindow window)
    {
        if (ContextField?.GetValue(window) is SessionContext context)
            window.Title = $"SoulBuddy · {context.LocalPlayer.DisplayName}";

        var sessionHeading = window.GetVisualDescendants().OfType<TextBlock>().FirstOrDefault(text =>
            string.Equals(text.Text, "AKTIVE SESSION", StringComparison.Ordinal));

        if (sessionHeading?.Parent is StackPanel sessionSection)
        {
            sessionSection.IsVisible = false;
            sessionSection.Width = 0;
            sessionSection.Margin = new Thickness(0);
        }

        var sessionGrid = sessionHeading?.GetVisualAncestors().OfType<Grid>().FirstOrDefault();
        if (sessionGrid is not null && sessionGrid.ColumnDefinitions.Count >= 5)
        {
            sessionGrid.ColumnDefinitions[0].Width = new GridLength(0);
            sessionGrid.ColumnDefinitions[1].Width = new GridLength(0);
            sessionGrid.ColumnDefinitions[2].Width = new GridLength(1, GridUnitType.Star);
            sessionGrid.ColumnDefinitions[3].Width = new GridLength(1);
            sessionGrid.ColumnDefinitions[4].Width = new GridLength(1, GridUnitType.Star);
        }
    }

    private static async Task StartForWindowAsync(MainWindow window)
    {
        await Task.Delay(350);
        if (SoullockeLaunchSettings.Enabled ||
            ContextField?.GetValue(window) is not SessionContext context ||
            NetworkField?.GetValue(window) is not SoulBuddyNetworkService network)
            return;

        var playerName = context.LocalPlayer.DisplayName;
        try
        {
            network.PrepareJoin(LanDiscoveryChannel, playerName, network.JoinAddress);
            var searchUntil = DateTimeOffset.UtcNow + TimeSpan.FromMilliseconds(1800 + Random.Shared.Next(0, 2200));
            while (DateTimeOffset.UtcNow < searchUntil)
            {
                await Task.Delay(200);
                if (network.State == SoulBuddyNetworkState.Connected) return;
            }
            if (network.State != SoulBuddyNetworkState.Connected)
                network.PrepareHost(LanDiscoveryChannel, playerName);
        }
        catch
        {
            await Task.Delay(Random.Shared.Next(350, 900));
            if (network.State != SoulBuddyNetworkState.Connected)
                network.PrepareJoin(LanDiscoveryChannel, playerName, network.JoinAddress);
        }
    }
}
