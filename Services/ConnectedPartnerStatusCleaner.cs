using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

/// <summary>
/// Hides the legacy partner headline while the real network status below it
/// already reports an active connection.
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
        var connectedText = connected && !string.IsNullOrWhiteSpace(network?.RemotePlayerName)
            ? $"🟢 {network.RemotePlayerName} verbunden"
            : string.Empty;

        foreach (var window in desktop.Windows)
        {
            foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
            {
                var isLegacyHeadline = text.Text is "🟡 Nicht verbunden" or
                    "🟡 Lokal eingetragen" ||
                    (!string.IsNullOrEmpty(connectedText) &&
                     string.Equals(text.Text, connectedText, StringComparison.Ordinal));

                if (!isLegacyHeadline)
                {
                    continue;
                }

                if (connected)
                {
                    text.IsVisible = false;
                    text.Height = 0;
                    text.Margin = new Thickness(0);
                }
                else
                {
                    text.Text = "🟡 Nicht verbunden";
                    text.IsVisible = true;
                    text.Height = double.NaN;
                    text.Margin = new Thickness(0);
                    text.Foreground = new SolidColorBrush(Color.Parse("#FBBF24"));
                }
            }
        }
    }
}
