using System.Runtime.CompilerServices;
using System.Xml.Linq;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

internal static class SessionLinkCopyIconInjector
{
    private static readonly HashSet<Button> AppliedButtons = [];
    private static DispatcherTimer? _discoveryTimer;
    private static Geometry? _copyGeometry;
    private static bool _copyGeometryResolved;

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

            // Keep the SoulLocke URL visible, but replace the read-only TextBox with
            // normal text now that copying is handled by the dedicated icon button.
            var linkBox = sessionLinkRow.Children
                .OfType<TextBox>()
                .FirstOrDefault();
            if (linkBox is not null)
            {
                var plainLink = new TextBlock
                {
                    Text = linkBox.Text,
                    FontSize = 10,
                    Foreground = new SolidColorBrush(Color.Parse("#E2E8F0")),
                    VerticalAlignment = VerticalAlignment.Center,
                    HorizontalAlignment = HorizontalAlignment.Stretch,
                    TextTrimming = TextTrimming.CharacterEllipsis
                };
                ToolTip.SetTip(plainLink, linkBox.Text);

                sessionLinkRow.Children.Remove(linkBox);
                Grid.SetColumn(plainLink, 1);
                sessionLinkRow.Children.Add(plainLink);
            }

            sessionLinkRow.ColumnDefinitions = new ColumnDefinitions("Auto,*,Auto");
            sessionLinkRow.ColumnSpacing = 7;
            Grid.SetColumn(copyButton, 2);

            // Session.Name is currently empty for SoulLocke sessions. MainWindow used
            // to add a TextBlock for it anyway, which left an empty line between the
            // section heading and the Session-Link row.
            if (sessionLinkRow.Parent is StackPanel sessionStack)
            {
                var emptySessionName = sessionStack.Children
                    .OfType<TextBlock>()
                    .FirstOrDefault(text => string.IsNullOrWhiteSpace(text.Text));
                if (emptySessionName is not null)
                    sessionStack.Children.Remove(emptySessionName);

                sessionStack.Spacing = 0;
            }

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
        var geometry = ResolveCopyGeometry();
        if (geometry is not null)
        {
            return new Avalonia.Controls.Shapes.Path
            {
                Data = geometry,
                Width = 14,
                Height = 14,
                Stretch = Stretch.Uniform,
                Stroke = new SolidColorBrush(Color.Parse("#E2E8F0")),
                StrokeThickness = 1.5,
                StrokeLineCap = PenLineCap.Round,
                StrokeJoin = PenLineJoin.Round,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        return new TextBlock
        {
            Text = "⧉",
            FontSize = 14,
            Foreground = new SolidColorBrush(Color.Parse("#E2E8F0")),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center
        };
    }

    private static Geometry? ResolveCopyGeometry()
    {
        if (_copyGeometryResolved)
            return _copyGeometry;

        _copyGeometryResolved = true;

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
                        "copy.svg");
                    if (!File.Exists(path))
                        continue;

                    try
                    {
                        var document = XDocument.Load(path);
                        var pathElement = document
                            .Descendants()
                            .FirstOrDefault(element => string.Equals(
                                element.Name.LocalName,
                                "path",
                                StringComparison.OrdinalIgnoreCase));
                        var pathData = pathElement?.Attribute("d")?.Value;
                        if (string.IsNullOrWhiteSpace(pathData))
                            continue;

                        _copyGeometry = Geometry.Parse(pathData);
                        return _copyGeometry;
                    }
                    catch (IOException)
                    {
                    }
                    catch (UnauthorizedAccessException)
                    {
                    }
                    catch (System.Xml.XmlException)
                    {
                    }
                    catch (FormatException)
                    {
                    }
                }

                directory = directory.Parent;
            }
        }

        return null;
    }
}
