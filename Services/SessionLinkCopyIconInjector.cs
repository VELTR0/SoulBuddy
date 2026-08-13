using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

internal static class SessionLinkCopyIconInjector
{
    private static readonly HashSet<Button> AppliedButtons = [];
    private static DispatcherTimer? _discoveryTimer;
    private static Bitmap? _copyBitmap;
    private static bool _copyBitmapResolved;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _discoveryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(150)
            };
            _discoveryTimer.Tick += (_, _) => ApplyToOpenWindows();
            _discoveryTimer.Start();
            ApplyToOpenWindows();
        });
    }

    private static void ApplyToOpenWindows()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (var window in desktop.Windows)
        {
            var sessionLinkRow = window
                .GetVisualDescendants()
                .OfType<Grid>()
                .FirstOrDefault(grid => grid.Children
                    .OfType<TextBlock>()
                    .Any(text => string.Equals(
                        text.Text,
                        "Session Link:",
                        StringComparison.Ordinal)));

            if (sessionLinkRow is null)
                continue;

            var copyButton = sessionLinkRow.Children
                .OfType<Button>()
                .FirstOrDefault(button =>
                    button.Content is string text &&
                    string.Equals(text, "Kopieren", StringComparison.Ordinal));

            if (copyButton is null || !AppliedButtons.Add(copyButton))
                continue;

            copyButton.Content = CreateCopyIcon();
            copyButton.Width = 30;
            copyButton.MinWidth = 30;
            copyButton.Padding = new Thickness(6, 4);
            copyButton.HorizontalContentAlignment = HorizontalAlignment.Center;
            copyButton.VerticalContentAlignment = VerticalAlignment.Center;
            ToolTip.SetTip(copyButton, "Session-Link kopieren");
        }
    }

    private static Control CreateCopyIcon()
    {
        var bitmap = ResolveCopyBitmap();
        if (bitmap is not null)
        {
            return new Image
            {
                Source = bitmap,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        // Keep the button icon-only even when the local image has not yet been
        // copied into a build output (for example on CI where the private asset
        // is not present in the repository).
        return new TextBlock
        {
            Text = "⧉",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#E2E8F0")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Bitmap? ResolveCopyBitmap()
    {
        if (_copyBitmapResolved)
            return _copyBitmap;

        _copyBitmapResolved = true;

        foreach (var root in new[]
                 {
                     AppContext.BaseDirectory,
                     Directory.GetCurrentDirectory()
                 }.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directory = new DirectoryInfo(Path.GetFullPath(root));
            while (directory is not null)
            {
                foreach (var resourceDirectory in new[] { "Ressources", "Resources" })
                {
                    var path = Path.Combine(
                        directory.FullName,
                        resourceDirectory,
                        "copy.png");
                    if (!File.Exists(path))
                        continue;

                    try
                    {
                        using var stream = File.OpenRead(path);
                        _copyBitmap = new Bitmap(stream);
                        return _copyBitmap;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                }

                directory = directory.Parent;
            }
        }

        return null;
    }
}
