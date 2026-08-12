using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Input.Platform;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

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

            var tabControl = FindLiveDetailsTabControl(window);
            if (tabControl is null)
                continue;

            var state = new StreamWindowState(window, tabControl);
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
                    .Select(item => item.Header?.ToString() ?? string.Empty)
                    .ToArray();

                return headers.Contains("Live", StringComparer.Ordinal) &&
                       headers.Contains("Details", StringComparer.Ordinal);
            });
    }

    private static void Detach(Window window)
    {
        if (!WindowStates.Remove(window, out var state))
            return;

        _ = state.DisposeAsync().AsTask();
    }

    private sealed class StreamWindowState : IAsyncDisposable
    {
        private readonly Window _window;
        private readonly LocalStreamService _streamService = new();
        private readonly DispatcherTimer _incomingDebounceTimer;
        private readonly TextBox _incomingAddressBox;
        private readonly TextBlock _incomingStatusText;
        private readonly TextBox _outgoingAddressBox;
        private readonly TextBlock _outgoingStatusText;
        private readonly Button _startButton;
        private readonly Button _copyButton;
        private bool _startOperationRunning;

        public StreamWindowState(Window window, TabControl tabs)
        {
            _window = window;
            _incomingAddressBox = CreateTextBox(
                "http://127.0.0.1:PORT/stream",
                isReadOnly: false);
            _incomingStatusText = Text(
                _streamService.IncomingStatus,
                10,
                FontWeight.Normal,
                "#94A3B8");
            _outgoingAddressBox = CreateTextBox(
                string.Empty,
                isReadOnly: true);
            _outgoingAddressBox.Text = "Noch nicht gestartet";
            _outgoingStatusText = Text(
                _streamService.OutgoingStatus,
                10,
                FontWeight.Normal,
                "#94A3B8");

            _startButton = CreateButton("Start");
            _copyButton = CreateButton("Kopieren");
            _copyButton.IsEnabled = false;

            _incomingDebounceTimer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(650)
            };
            _incomingDebounceTimer.Tick += OnIncomingDebounceTick;
            _incomingAddressBox.TextChanged += OnIncomingAddressChanged;
            _startButton.Click += OnStartButtonClick;
            _copyButton.Click += OnCopyButtonClick;
            _streamService.StatusChanged += OnStreamStatusChanged;

            tabs.Items.Add(new TabItem
            {
                Header = "Stream",
                Content = new ScrollViewer
                {
                    Content = BuildPanel(),
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled
                }
            });
        }

        private Control BuildPanel()
        {
            var root = new StackPanel
            {
                Spacing = 12,
                Margin = new Thickness(2)
            };

            root.Children.Add(Text(
                "Lokales Streaming",
                15,
                FontWeight.Bold,
                "#F8FAFC"));
            root.Children.Add(Text(
                "Für den ersten Test läuft der Videostream direkt zwischen zwei SoulBuddy-Instanzen auf demselben PC.",
                10,
                FontWeight.Normal,
                "#94A3B8",
                wrap: true));

            var incomingPanel = new StackPanel { Spacing = 6 };
            incomingPanel.Children.Add(Text(
                "Stream ansehen",
                11,
                FontWeight.SemiBold,
                "#E2E8F0"));
            incomingPanel.Children.Add(_incomingAddressBox);
            incomingPanel.Children.Add(_incomingStatusText);
            root.Children.Add(Card(incomingPanel));

            var outgoingPanel = new StackPanel { Spacing = 6 };
            outgoingPanel.Children.Add(Text(
                "Eigenen oberen Bildschirm streamen",
                11,
                FontWeight.SemiBold,
                "#E2E8F0"));

            var addressRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 6
            };
            addressRow.Children.Add(_outgoingAddressBox);
            Grid.SetColumn(_copyButton, 1);
            addressRow.Children.Add(_copyButton);
            outgoingPanel.Children.Add(addressRow);

            var actionRow = new Grid
            {
                ColumnDefinitions = new ColumnDefinitions("*,Auto"),
                ColumnSpacing = 8
            };
            actionRow.Children.Add(_outgoingStatusText);
            Grid.SetColumn(_startButton, 1);
            actionRow.Children.Add(_startButton);
            outgoingPanel.Children.Add(actionRow);
            root.Children.Add(Card(outgoingPanel));

            root.Children.Add(Text(
                "Der empfangene Stream erscheint als 128×96-Picture-in-Picture oben rechts im oberen DeSmuME-Bildschirm. SoulBuddy-Meldungen werden weiterhin darüber gezeichnet.",
                9,
                FontWeight.Normal,
                "#7C8BA1",
                wrap: true));

            return root;
        }

        private void OnIncomingAddressChanged(object? sender, TextChangedEventArgs eventArgs)
        {
            _incomingDebounceTimer.Stop();
            _incomingDebounceTimer.Start();
        }

        private async void OnIncomingDebounceTick(object? sender, EventArgs eventArgs)
        {
            _incomingDebounceTimer.Stop();
            await _streamService.SetIncomingUrlAsync(_incomingAddressBox.Text);
            RefreshUi();
        }

        private async void OnStartButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
        {
            if (_startOperationRunning)
                return;

            _startOperationRunning = true;
            _startButton.IsEnabled = false;

            try
            {
                if (_streamService.IsOutgoingRunning)
                {
                    await _streamService.StopOutgoingAsync();
                }
                else
                {
                    await _streamService.StartOutgoingAsync();
                }
            }
            catch (Exception ex)
            {
                _outgoingStatusText.Text = $"Stream konnte nicht gestartet werden: {ex.Message}";
                _outgoingStatusText.Foreground = Brush("#FCA5A5");
            }
            finally
            {
                _startOperationRunning = false;
                _startButton.IsEnabled = true;
                RefreshUi();
            }
        }

        private async void OnCopyButtonClick(object? sender, Avalonia.Interactivity.RoutedEventArgs eventArgs)
        {
            var url = _streamService.OutgoingUrl;
            if (string.IsNullOrWhiteSpace(url))
                return;

            var clipboard = TopLevel.GetTopLevel(_window)?.Clipboard;
            if (clipboard is not null)
                await clipboard.SetTextAsync(url);
        }

        private void OnStreamStatusChanged(object? sender, EventArgs eventArgs)
        {
            Dispatcher.UIThread.Post(RefreshUi);
        }

        private void RefreshUi()
        {
            _incomingStatusText.Text = _streamService.IncomingStatus;
            _incomingStatusText.Foreground = Brush(
                _streamService.IncomingStatus.Contains("angezeigt", StringComparison.OrdinalIgnoreCase)
                    ? "#A7F3D0"
                    : "#94A3B8");

            _outgoingStatusText.Text = _streamService.OutgoingStatus;
            _outgoingStatusText.Foreground = Brush(
                _streamService.IsOutgoingRunning
                    ? "#A7F3D0"
                    : "#94A3B8");

            _outgoingAddressBox.Text =
                _streamService.OutgoingUrl ?? "Noch nicht gestartet";
            _copyButton.IsEnabled = !string.IsNullOrWhiteSpace(
                _streamService.OutgoingUrl);
            _startButton.Content = _streamService.IsOutgoingRunning
                ? "Stop"
                : "Start";
        }

        public async ValueTask DisposeAsync()
        {
            _incomingDebounceTimer.Stop();
            _incomingDebounceTimer.Tick -= OnIncomingDebounceTick;
            _incomingAddressBox.TextChanged -= OnIncomingAddressChanged;
            _startButton.Click -= OnStartButtonClick;
            _copyButton.Click -= OnCopyButtonClick;
            _streamService.StatusChanged -= OnStreamStatusChanged;
            await _streamService.DisposeAsync();
        }
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

    private static TextBox CreateTextBox(string placeholder, bool isReadOnly) => new()
    {
        PlaceholderText = placeholder,
        IsReadOnly = isReadOnly,
        FontSize = 10,
        Foreground = Brush("#E2E8F0"),
        Background = Brush("#0F1829"),
        BorderBrush = Brush("#344763"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(6),
        Padding = new Thickness(7, 5),
        HorizontalAlignment = HorizontalAlignment.Stretch
    };

    private static Button CreateButton(string content) => new()
    {
        Content = content,
        Padding = new Thickness(9, 5),
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
}
