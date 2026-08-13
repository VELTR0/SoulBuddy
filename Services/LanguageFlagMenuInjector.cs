using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

internal static class LanguageFlagMenuInjector
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
            _timer.Tick += (_, _) => ApplyToOpenWindows();
            _timer.Start();
            ApplyToOpenWindows();
        });
    }

    private static void ApplyToOpenWindows()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        foreach (var window in desktop.Windows)
        {
            foreach (var button in window.GetVisualDescendants().OfType<Button>())
            {
                if (button.Flyout is not MenuFlyout menu)
                    continue;

                foreach (var item in menu.Items.OfType<MenuItem>())
                {
                    var header = item.Header?.ToString() ?? string.Empty;
                    var flag = ExtractFlag(header);
                    if (flag is not null && !string.Equals(header, flag, StringComparison.Ordinal))
                        item.Header = flag;
                }
            }
        }
    }

    private static string? ExtractFlag(string value)
    {
        if (value.Contains("🇬🇧", StringComparison.Ordinal)) return "🇬🇧";
        if (value.Contains("🇩🇪", StringComparison.Ordinal)) return "🇩🇪";
        if (value.Contains("🇫🇷", StringComparison.Ordinal)) return "🇫🇷";
        if (value.Contains("🇪🇸", StringComparison.Ordinal)) return "🇪🇸";
        if (value.Contains("🇮🇹", StringComparison.Ordinal)) return "🇮🇹";
        if (value.Contains("🇯🇵", StringComparison.Ordinal)) return "🇯🇵";
        return null;
    }
}
