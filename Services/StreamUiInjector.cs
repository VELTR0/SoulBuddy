using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Avalonia.VisualTree;
using SoulBuddy.ViewModels;

namespace SoulBuddy.Services;

internal static class StreamUiInjector
{
    private static readonly Dictionary<Window, StreamWindowState> WindowStates = [];
    private static DispatcherTimer? _discoveryTimer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _discoveryTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(400)
            };
            _discoveryTimer.Tick += (_, _) => DiscoverWindows();
            _discoveryTimer.Start();
            DiscoverWindows();
        });
    }

    private static void DiscoverWindows()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (var window in desktop.Windows)
        {
            if (WindowStates.ContainsKey(window))
                continue;

            var tabs = FindLiveDetailsTabControl(window);
            var livePanel = tabs is null ? null : FindLivePanel(tabs);
            if (tabs is null || livePanel is null)
                continue;

            var state = new StreamWindowState(window, livePanel);
            WindowStates[window] = state;
            window.Closed += (_, _) => Detach(window);
        }
    }

    private static TabControl? FindLiveDetailsTabControl(Window window)
    {
        return window
            .GetVisualDescendants()
            .OfType<TabControl>()
            .FirstOrDefault(control =>
            {
                var headers = control.Items
                    .OfType<TabItem>()
                    .Select(item => item.Header?.ToString())
                    .ToArray();

                return headers.Any(header => LocalizationService.IsTranslationOf(header, "Live")) &&
                       headers.Any(header => LocalizationService.IsTranslationOf(header, "Details"));
            });
    }

    private static StackPanel? FindLivePanel(TabControl tabs)
    {
        var liveTab = tabs.Items
            .OfType<TabItem>()
            .FirstOrDefault(item =>
                LocalizationService.IsTranslationOf(item.Header?.ToString(), "Live"));

        return (liveTab?.Content as ScrollViewer)?.Content as StackPanel;
    }

    private static void Detach(Window window)
    {
        if (!WindowStates.Remove(window, out var state))
            return;

        _ = state.DisposeAsync().AsTask();
    }

    private sealed class StreamWindowState : IAsyncDisposable
    {
        private const int PreviewWidth = 256;
        private const int PreviewHeight = 192;

        private readonly MainWindowViewModel? _viewModel;
        private readonly LocalStreamService _streamService = new();
        private readonly LanStreamDiscoveryService _lanDiscovery = new();
        private readonly WriteableBitmap _ownPreviewBitmap = CreatePreviewBitmap();
        private readonly WriteableBitmap _partnerPreviewBitmap = CreatePreviewBitmap();
        private readonly Image _ownPreviewImage;
        private readonly Image _partnerPreviewImage;
        private readonly TextBlock _ownPreviewPlaceholder;
        private readonly TextBlock _partnerPreviewPlaceholder;
        private readonly TextBlock _partnerActivityTitle;
        private readonly TextBlock _partnerActivityText;
        private readonly Border _partnerActivityCard;
        private readonly Button _startButton;
        private readonly CheckBox _showOverlayCheckBox;
        private readonly CheckBox _showGuiStreamsCheckBox;
        private readonly Control _previewContainer;
        private readonly string _renderHiddenPath;

        private CancellationTokenSource? _partnerDiscoveryCancellation;
        private Task? _partnerDiscoveryTask;
        private CancellationTokenSource? _partnerActivityCancellation;
        private Task? _partnerActivityTask;
        private bool _startOperationRunning;
        private bool _partnerSearching;
        private bool _partnerConnected;
        private bool _disposed;
        private string _partnerActivityTitleSource = string.Empty;
        private string _partnerActivityTextSource = string.Empty;

        public StreamWindowState(Window window, StackPanel livePanel)
        {
            _viewModel = window.DataContext as MainWindowViewModel;
            _renderHiddenPath = LuaLaunchContext.ScopePath(
                Path.Combine(FindRuntimeDirectory(), "stream-render.hidden"));
            TryDeleteFile(_renderHiddenPath);

            _ownPreviewImage = CreatePreviewImage(_ownPreviewBitmap);
            _partnerPreviewImage = CreatePreviewImage(_partnerPreviewBitmap);
            _ownPreviewImage.IsVisible = false;
            _partnerPreviewImage.IsVisible = false;

            _ownPreviewPlaceholder = PreviewPlaceholder("Nicht gestartet");
            _partnerPreviewPlaceholder = PreviewPlaceholder("Warte auf Partner-Stream");

            _partnerActivityTitle = Text(
                "Partner-Aktivität wird geladen …",
                12,
                FontWeight.Bold,
                "#93C5FD",
                wrap: true);
            _partnerActivityText = Text(
                string.Empty,
                11,
                FontWeight.Normal,
                "#E2E8F0",
                wrap: true);
            _partnerActivityText.LineHeight = 17;
            _partnerActivityCard = BuildPartnerActivityCard();
            _partnerActivityCard.IsVisible = false;

            _startButton = CreateButton(StartButtonContent(isRunning: false));
            _startButton.HorizontalAlignment = HorizontalAlignment.Left;
            _startButton.Click += OnStartButtonClick;

            _showOverlayCheckBox = CreateVisibilityCheckBox(
                "Stream in DeSmuMe anzeigen");
            _showGuiStreamsCheckBox = CreateVisibilityCheckBox(
                "Streams hier anzeigen");
            _showOverlayCheckBox.Click += OnOverlayVisibilityClick;
            _showGuiStreamsCheckBox.Click += OnGuiVisibilityClick;

            _streamService.OutgoingFrameChanged += OnOwnFrameChanged;
            _streamService.IncomingFrameChanged += OnPartnerFrameChanged;
            _streamService.StatusChanged += OnStreamStatusChanged;
            if (_viewModel is not null)
                _viewModel.PropertyChanged += OnViewModelPropertyChanged;

            _previewContainer = BuildPreviewContainer();

            // The original Live view already contains the local activity title/text.
            // Label it explicitly, add the partner activity below it, then place the
            // former Stream-tab content at the very bottom of the same Live panel.
            livePanel.Children.Insert(0, Text(
                "Du",
                10,
                FontWeight.SemiBold,
                "#93C5FD"));
            livePanel.Children.Add(_partnerActivityCard);
            livePanel.Children.Add(BuildStreamingSection());

            PublishLocalActivity();
        }

        private Border BuildPartnerActivityCard()
        {
            var panel = new StackPanel { Spacing = 6 };
            panel.Children.Add(Text(
                "Partner",
                10,
                FontWeight.SemiBold,
                "#93C5FD"));
            panel.Children.Add(_partnerActivityTitle);
            panel.Children.Add(_partnerActivityText);
            return Card(panel);
        }

        private Control BuildStreamingSection()
        {
            var root = new StackPanel
            {
                Spacing = 10,
                Margin = new Thickness(0, 10, 0, 0)
            };

            root.Children.Add(new Border
            {
                Height = 1,
                Background = Brush("#2B3C58"),
                Margin = new Thickness(0, 2, 0, 5)
            });
            root.Children.Add(Text(
                "Streaming",
                15,
                FontWeight.Bold,
                "#F8FAFC"));

            var controls = new StackPanel { Spacing = 9 };
            controls.Children.Add(_startButton);
            controls.Children.Add(_showOverlayCheckBox);
            controls.Children.Add(_showGuiStreamsCheckBox);
            root.Children.Add(Card(controls));
            root.Children.Add(_previewContainer);
            return root;
        }

        private Control BuildPreviewContainer()
        {
            var grid = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,*"),
                ColumnSpacing = 10
            };

            var ownCard = PreviewCard(
                "Du",
                _ownPreviewImage,
                _ownPreviewPlaceholder);
            var partnerCard = PreviewCard(
                "Partner",
                _partnerPreviewImage,
                _partnerPreviewPlaceholder);

            grid.Children.Add(ownCard);
            Grid.SetColumn(partnerCard, 1);
            grid.Children.Add(partnerCard);
            return grid;
        }

        private static Border PreviewCard(
            string title,
            Image image,
            TextBlock placeholder)
        {
            var panel = new StackPanel { Spacing = 7 };
            panel.Children.Add(Text(
                title,
                10,
                FontWeight.SemiBold,
                "#E2E8F0"));

            var viewport = new Grid
            {
                Height = 132,
                Background = Brush("#09101D")
            };
            viewport.Children.Add(image);
            viewport.Children.Add(placeholder);
            panel.Children.Add(viewport);
            return Card(panel);
        }

        private async void OnStartButtonClick(
            object? sender,
            Avalonia.Interactivity.RoutedEventArgs eventArgs)
        {
            if (_startOperationRunning)
                return;

            _startOperationRunning = true;
            _startButton.IsEnabled = false;

            try
            {
                if (_streamService.IsOutgoingRunning)
                    await StopStreamingSessionAsync();
                else
                    await StartStreamingSessionAsync();
            }
            catch (Exception ex)
            {
                _ownPreviewImage.IsVisible = false;
                _ownPreviewPlaceholder.IsVisible = true;
                _ownPreviewPlaceholder.Text = $"Stream konnte nicht gestartet werden: {ex.Message}";
            }
            finally
            {
                _startOperationRunning = false;
                _startButton.IsEnabled = true;
                RefreshUi();
            }
        }

        private async Task StartStreamingSessionAsync()
        {
            PublishLocalActivity();
            var localUrl = await _streamService.StartOutgoingAsync();

            try
            {
                await _lanDiscovery.StartAdvertisingAsync(localUrl);
                StartPartnerDiscoveryLoop();
                StartPartnerActivityLoop();
            }
            catch
            {
                await StopPartnerActivityLoopAsync();
                await _lanDiscovery.StopAdvertisingAsync();
                await _streamService.StopOutgoingAsync();
                throw;
            }
        }

        private async Task StopStreamingSessionAsync()
        {
            await StopPartnerDiscoveryLoopAsync();
            await StopPartnerActivityLoopAsync();
            _partnerSearching = false;
            _partnerConnected = false;
            ClearPartnerActivity();

            await _streamService.SetIncomingUrlAsync(null);
            await _lanDiscovery.StopAdvertisingAsync();
            await _streamService.StopOutgoingAsync();

            _partnerPreviewPlaceholder.Text = "Warte auf Partner-Stream";
            RefreshUi();
        }

        private void StartPartnerDiscoveryLoop()
        {
            _partnerDiscoveryCancellation?.Cancel();
            _partnerDiscoveryCancellation?.Dispose();

            _partnerSearching = true;
            _partnerConnected = false;
            _partnerDiscoveryCancellation = new CancellationTokenSource();
            _partnerDiscoveryTask = DiscoverPartnerLoopAsync(
                _partnerDiscoveryCancellation.Token);
            RefreshUi();
        }

        private async Task DiscoverPartnerLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var url = await _lanDiscovery.DiscoverAsync(cancellationToken);
                    if (!string.IsNullOrWhiteSpace(url))
                    {
                        await _streamService.SetIncomingUrlAsync(url);
                        _partnerSearching = false;
                        Dispatcher.UIThread.Post(RefreshUi);
                        return;
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Discovery is best-effort. Keep searching until a partner is found.
                }

                try
                {
                    await Task.Delay(500, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private void StartPartnerActivityLoop()
        {
            _partnerActivityCancellation?.Cancel();
            _partnerActivityCancellation?.Dispose();

            _partnerActivityCancellation = new CancellationTokenSource();
            _partnerActivityTask = PartnerActivityLoopAsync(
                _partnerActivityCancellation.Token);
        }

        private async Task PartnerActivityLoopAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var activity = await _lanDiscovery.QueryPartnerActivityAsync(cancellationToken);
                    if (activity is not null)
                    {
                        Dispatcher.UIThread.Post(() =>
                        {
                            _partnerActivityTitleSource = activity.Value.Title;
                            _partnerActivityTextSource = activity.Value.Text;
                            _partnerActivityTitle.Text = string.IsNullOrWhiteSpace(activity.Value.Title)
                                ? "Partner-Aktivität wird geladen …"
                                : activity.Value.Title;
                            _partnerActivityText.Text = activity.Value.Text;
                            UpdatePartnerActivityVisibility();
                        });
                    }
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
                catch
                {
                    // Live activity is supplemental. Video reconnect/discovery must
                    // continue even if a multicast activity request is dropped.
                }

                try
                {
                    await Task.Delay(650, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }

        private async Task StopPartnerDiscoveryLoopAsync()
        {
            var cancellation = _partnerDiscoveryCancellation;
            var task = _partnerDiscoveryTask;
            _partnerDiscoveryCancellation = null;
            _partnerDiscoveryTask = null;

            if (cancellation is null)
                return;

            cancellation.Cancel();
            await IgnoreCancellationAsync(task);
            cancellation.Dispose();
        }

        private async Task StopPartnerActivityLoopAsync()
        {
            var cancellation = _partnerActivityCancellation;
            var task = _partnerActivityTask;
            _partnerActivityCancellation = null;
            _partnerActivityTask = null;

            if (cancellation is null)
                return;

            cancellation.Cancel();
            await IgnoreCancellationAsync(task);
            cancellation.Dispose();
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs eventArgs)
        {
            if (eventArgs.PropertyName is nameof(MainWindowViewModel.LiveEncounterTitle) or
                nameof(MainWindowViewModel.LiveEncounterText))
            {
                PublishLocalActivity();
            }
        }

        private void PublishLocalActivity()
        {
            if (_viewModel is null)
                return;

            _lanDiscovery.SetLocalActivity(
                _viewModel.LiveEncounterTitle,
                _viewModel.LiveEncounterText);
        }

        private void OnOverlayVisibilityClick(
            object? sender,
            Avalonia.Interactivity.RoutedEventArgs eventArgs)
        {
            ApplyOverlayVisibility();
        }

        private void OnGuiVisibilityClick(
            object? sender,
            Avalonia.Interactivity.RoutedEventArgs eventArgs)
        {
            _previewContainer.IsVisible = _showGuiStreamsCheckBox.IsChecked != false;
        }

        private void ApplyOverlayVisibility()
        {
            var visible = _showOverlayCheckBox.IsChecked != false;
            if (visible)
            {
                TryDeleteFile(_renderHiddenPath);
                return;
            }

            try
            {
                var directory = Path.GetDirectoryName(_renderHiddenPath);
                if (!string.IsNullOrWhiteSpace(directory))
                    Directory.CreateDirectory(directory);
                File.WriteAllText(_renderHiddenPath, "1");
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        private void OnOwnFrameChanged(byte[]? frame)
        {
            Dispatcher.UIThread.Post(() =>
                ApplyPreviewFrame(
                    _ownPreviewBitmap,
                    _ownPreviewImage,
                    _ownPreviewPlaceholder,
                    frame,
                    _streamService.IsOutgoingRunning
                        ? "Warte auf Videoframes"
                        : "Nicht gestartet"));
        }

        private void OnPartnerFrameChanged(byte[]? frame)
        {
            Dispatcher.UIThread.Post(() =>
                ApplyPreviewFrame(
                    _partnerPreviewBitmap,
                    _partnerPreviewImage,
                    _partnerPreviewPlaceholder,
                    frame,
                    _partnerConnected
                        ? "Kein Partnerbild"
                        : "Warte auf Partner-Stream"));
        }

        private static void ApplyPreviewFrame(
            WriteableBitmap bitmap,
            Image image,
            TextBlock placeholder,
            byte[]? frame,
            string emptyText)
        {
            if (frame is null || !TryWriteGdFrame(bitmap, frame))
            {
                image.IsVisible = false;
                placeholder.Text = emptyText;
                placeholder.IsVisible = true;
                return;
            }

            image.IsVisible = true;
            placeholder.IsVisible = false;
            image.InvalidateVisual();
        }

        private void OnStreamStatusChanged(object? sender, EventArgs eventArgs)
        {
            Dispatcher.UIThread.Post(RefreshUi);
        }

        private void RefreshUi()
        {
            if (_disposed)
                return;

            _partnerConnected = _streamService.IncomingStatus.StartsWith(
                "Partner-Stream verbunden",
                StringComparison.Ordinal);

            if (!_partnerConnected && !_partnerSearching)
                _partnerPreviewPlaceholder.Text = "Warte auf Partner-Stream";

            UpdatePartnerActivityVisibility();
            _startButton.Content = StartButtonContent(_streamService.IsOutgoingRunning);
        }

        private void UpdatePartnerActivityVisibility()
        {
            _partnerActivityCard.IsVisible =
                _partnerConnected &&
                (!string.IsNullOrWhiteSpace(_partnerActivityTitleSource) ||
                 !string.IsNullOrWhiteSpace(_partnerActivityTextSource));
        }

        private void ClearPartnerActivity()
        {
            _partnerActivityTitleSource = string.Empty;
            _partnerActivityTextSource = string.Empty;
            _partnerActivityTitle.Text = "Partner-Aktivität wird geladen …";
            _partnerActivityText.Text = string.Empty;
            _partnerActivityCard.IsVisible = false;
        }

        public async ValueTask DisposeAsync()
        {
            if (_disposed)
                return;

            _disposed = true;
            _startButton.Click -= OnStartButtonClick;
            _showOverlayCheckBox.Click -= OnOverlayVisibilityClick;
            _showGuiStreamsCheckBox.Click -= OnGuiVisibilityClick;
            _streamService.OutgoingFrameChanged -= OnOwnFrameChanged;
            _streamService.IncomingFrameChanged -= OnPartnerFrameChanged;
            _streamService.StatusChanged -= OnStreamStatusChanged;
            if (_viewModel is not null)
                _viewModel.PropertyChanged -= OnViewModelPropertyChanged;

            await StopPartnerDiscoveryLoopAsync();
            await StopPartnerActivityLoopAsync();
            await _lanDiscovery.DisposeAsync();
            await _streamService.DisposeAsync();

            TryDeleteFile(_renderHiddenPath);
            _ownPreviewBitmap.Dispose();
            _partnerPreviewBitmap.Dispose();
        }

        private static async Task IgnoreCancellationAsync(Task? task)
        {
            if (task is null)
                return;

            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }

        private static WriteableBitmap CreatePreviewBitmap() => new(
            new PixelSize(PreviewWidth, PreviewHeight),
            new Vector(96, 96),
            PixelFormats.Bgra8888,
            AlphaFormat.Opaque);

        private static Image CreatePreviewImage(WriteableBitmap bitmap) => new()
        {
            Source = bitmap,
            Stretch = Stretch.Uniform,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Stretch
        };

        private static CheckBox CreateVisibilityCheckBox(string label) => new()
        {
            Content = label,
            IsChecked = true,
            FontSize = 10,
            Foreground = Brush("#CBD5E1")
        };

        private static TextBlock PreviewPlaceholder(string text) => new()
        {
            Text = text,
            FontSize = 9,
            Foreground = Brush("#64748B"),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(8)
        };

        private static bool TryWriteGdFrame(WriteableBitmap bitmap, byte[] data)
        {
            if (data.Length < 11 ||
                data[0] != 0xFF ||
                data[1] != 0xFE ||
                data[6] != 1)
            {
                return false;
            }

            var sourceWidth = (data[2] << 8) | data[3];
            var sourceHeight = (data[4] << 8) | data[5];
            if (sourceWidth <= 0 || sourceHeight <= 0 ||
                11L + ((long)sourceWidth * sourceHeight * 4L) != data.Length)
            {
                return false;
            }

            using var framebuffer = bitmap.Lock();
            var row = new byte[PreviewWidth * 4];

            for (var y = 0; y < PreviewHeight; y++)
            {
                var sourceY = Math.Min(
                    sourceHeight - 1,
                    (int)((long)y * sourceHeight / PreviewHeight));

                for (var x = 0; x < PreviewWidth; x++)
                {
                    var sourceX = Math.Min(
                        sourceWidth - 1,
                        (int)((long)x * sourceWidth / PreviewWidth));
                    var sourceOffset = 11 + ((sourceY * sourceWidth + sourceX) * 4);
                    var targetOffset = x * 4;

                    row[targetOffset] = data[sourceOffset + 3];
                    row[targetOffset + 1] = data[sourceOffset + 2];
                    row[targetOffset + 2] = data[sourceOffset + 1];
                    row[targetOffset + 3] = 0xFF;
                }

                Marshal.Copy(
                    row,
                    0,
                    IntPtr.Add(framebuffer.Address, y * framebuffer.RowBytes),
                    row.Length);
            }

            return true;
        }
    }

    private static Control StartButtonContent(bool isRunning)
    {
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 7
        };
        content.Children.Add(Text(
            isRunning ? "■" : "▶",
            13,
            FontWeight.Bold,
            isRunning ? "#F87171" : "#4ADE80"));
        content.Children.Add(Text(
            isRunning ? "Stream stoppen" : "Stream starten",
            10,
            FontWeight.SemiBold,
            "#E2E8F0"));
        return content;
    }

    private static Border Card(Control child) => new()
    {
        Background = Brush("#151F33"),
        BorderBrush = Brush("#2B3C58"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(10),
        Child = child
    };

    private static Button CreateButton(object content) => new()
    {
        Content = content,
        Padding = new Thickness(11, 6),
        FontSize = 10,
        FontWeight = FontWeight.SemiBold,
        Background = Brush("#17243A"),
        Foreground = Brush("#E2E8F0"),
        BorderBrush = Brush("#344763"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6)
    };

    private static TextBlock Text(
        string value,
        double fontSize,
        FontWeight fontWeight,
        string color,
        bool wrap = false) => new()
    {
        Text = value,
        FontSize = fontSize,
        FontWeight = fontWeight,
        Foreground = Brush(color),
        TextWrapping = wrap ? TextWrapping.Wrap : TextWrapping.NoWrap
    };

    private static SolidColorBrush Brush(string color) =>
        new(Color.Parse(color));

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FindRuntimeDirectory()
    {
        var searchRoots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directory = new DirectoryInfo(Path.GetFullPath(root));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "appsettings.json")) &&
                    Directory.Exists(Path.Combine(
                        directory.FullName,
                        "collectors",
                        "desmume-gen4")))
                {
                    return Path.Combine(directory.FullName, "runtime");
                }

                directory = directory.Parent;
            }
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "runtime");
    }
}
