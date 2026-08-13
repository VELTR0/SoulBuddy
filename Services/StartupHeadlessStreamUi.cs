using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using SoulBuddy.Views;

namespace SoulBuddy.Services;

internal static class StartupHeadlessStreamUi
{
    private static readonly Dictionary<SessionSetupWindow, State> States = [];
    private static DispatcherTimer? _timer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
            Refresh();
        });
    }

    private static void Refresh()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (var window in desktop.Windows.OfType<SessionSetupWindow>())
        {
            if (!States.TryGetValue(window, out var state))
            {
                var showMainWindow = window
                    .GetVisualDescendants()
                    .OfType<CheckBox>()
                    .FirstOrDefault(checkBox => LocalizationService.IsTranslationOf(
                        checkBox.Content?.ToString(),
                        "Hauptfenster anzeigen"));

                if (showMainWindow?.Parent is not StackPanel parent)
                    continue;

                state = new State(window, parent, showMainWindow);
                States[window] = state;
                window.Closed += OnWindowClosed;
            }

            state.Refresh();
        }
    }

    private static void OnWindowClosed(object? sender, EventArgs eventArgs)
    {
        if (sender is not SessionSetupWindow window ||
            !States.Remove(window, out var state))
        {
            return;
        }

        window.Closed -= OnWindowClosed;
        state.Dispose();
    }

    private sealed class State : IDisposable
    {
        private readonly StackPanel _parent;
        private readonly CheckBox _showMainWindow;
        private readonly CheckBox _streamScreenToPartner;

        public State(
            SessionSetupWindow window,
            StackPanel parent,
            CheckBox showMainWindow)
        {
            _parent = parent;
            _showMainWindow = showMainWindow;
            _streamScreenToPartner = new CheckBox
            {
                IsChecked = false,
                IsVisible = false,
                IsEnabled = false,
                FontSize = 14,
                FontWeight = FontWeight.SemiBold,
                Foreground = Brush("#E2E8F0")
            };

            var index = parent.Children.IndexOf(showMainWindow);
            parent.Children.Insert(
                index >= 0 ? index + 1 : parent.Children.Count,
                _streamScreenToPartner);

            _showMainWindow.Click += OnOptionChanged;
            _streamScreenToPartner.Click += OnOptionChanged;
            Refresh();
        }

        private void OnOptionChanged(
            object? sender,
            Avalonia.Interactivity.RoutedEventArgs eventArgs)
        {
            Refresh();
        }

        public void Refresh()
        {
            _streamScreenToPartner.Content = StreamScreenToPartnerText();

            var headless = _showMainWindow.IsChecked == false;
            _streamScreenToPartner.IsVisible = headless;
            _streamScreenToPartner.IsEnabled = headless;

            if (!headless)
                _streamScreenToPartner.IsChecked = false;

            HeadlessStreamLaunchSettings.Configure(
                headless && _streamScreenToPartner.IsChecked == true);
        }

        public void Dispose()
        {
            _showMainWindow.Click -= OnOptionChanged;
            _streamScreenToPartner.Click -= OnOptionChanged;
            HeadlessStreamLaunchSettings.Configure(false);

            if (_parent.Children.Contains(_streamScreenToPartner))
                _parent.Children.Remove(_streamScreenToPartner);
        }
    }

    private static string StreamScreenToPartnerText() => LocalizationService.CurrentLanguage switch
    {
        AppLanguage.English => "Stream screen to Partner",
        AppLanguage.French => "Diffuser l’écran au partenaire",
        AppLanguage.Spanish => "Transmitir pantalla al compañero",
        AppLanguage.Italian => "Trasmetti lo schermo al partner",
        AppLanguage.Japanese => "画面をパートナーに配信",
        _ => "Bildschirm zum Partner streamen"
    };

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
}
