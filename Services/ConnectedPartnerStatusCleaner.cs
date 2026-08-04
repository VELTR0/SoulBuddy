using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

/// <summary>
/// Keeps the legacy partner headline in sync with the real network state.
/// The headline was originally created as static text in MainWindow and is
/// therefore not affected by the normal network-status binding.
/// </summary>
internal static class ConnectedPartnerStatusCleaner
{
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
            _timer.Tick += (_, _) => Update();
            _timer.Start();
        });
    }

    private static void Update()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        var network = SoulBuddyNetworkService.Current;
        var connected = network?.State == SoulBuddyNetworkState.Connected;

        foreach (var window in desktop.Windows)
        {
            foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
            {
                if (text.Text is not ("🟡 Nicht verbunden" or "🟡 Lokal eingetragen"))
                {
                    continue;
                }

                if (connected)
                {
                    text.Text = $"🟢 {network!.RemotePlayerName} verbunden";
                    text.Foreground = new SolidColorBrush(Color.Parse("#4ADE80"));
                }
                else
                {
                    text.Text = "🟡 Nicht verbunden";
                    text.Foreground = new SolidColorBrush(Color.Parse("#FBBF24"));
                }
            }
        }
    }
}
