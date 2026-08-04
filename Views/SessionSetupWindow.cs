using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Layout;
using Avalonia.Media;
using SoulBuddy.Models;
using SoulBuddy.Services;

namespace SoulBuddy.Views;

public sealed class SessionSetupWindow : Window
{
    private readonly SessionStore _sessionStore = new();
    private readonly TextBox _playerNameBox;
    private readonly CheckBox _soullockeCheckBox;
    private readonly TextBox _soullockeLinkBox;
    private readonly TextBox _soullockePasswordBox;
    private readonly TextBlock _statusText;
    private readonly Border _activePlayerCard;
    private readonly TextBlock _activePlayerTitle;
    private SessionContext? _activeContext;

    public SessionSetupWindow()
    {
        Title = "SoulBuddy";
        Width = 620;
        Height = 650;
        MinWidth = 520;
        MinHeight = 520;
        Background = Brush("#0B1220");

        _playerNameBox = CreateTextBox("Dein Spielername");
        _soullockeCheckBox = new CheckBox
        {
            Content = "Sync mit Soullocke",
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Foreground = Brush("#E2E8F0")
        };
        _soullockeCheckBox.IsCheckedChanged += (_, _) => UpdateSoullockeFieldState();

        _soullockeLinkBox = CreateTextBox("Soullocke-Link");
        _soullockePasswordBox = CreateTextBox("Soullocke-Passwort");
        _soullockePasswordBox.PasswordChar = '●';

        _statusText = Text(string.Empty, 13, FontWeight.Medium, "#CBD5E1");
        _statusText.TextWrapping = TextWrapping.Wrap;

        _activePlayerTitle = Text(string.Empty, 20, FontWeight.Bold, "#F8FAFC");
        _activePlayerCard = CreateCard(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text("Zuletzt verwendet", 12, FontWeight.Bold, "#93C5FD"),
                _activePlayerTitle,
                CreateButton("Mit diesem Namen starten", ContinueAsync, true)
            }
        });
        _activePlayerCard.IsVisible = false;

        Content = BuildLayout();
        UpdateSoullockeFieldState();
        Opened += OnOpened;
    }

    private Control BuildLayout()
    {
        var content = new StackPanel
        {
            Spacing = 18,
            Margin = new Thickness(34)
        };

        content.Children.Add(Text("SoulBuddy", 36, FontWeight.Bold, "#F8FAFC"));
        content.Children.Add(Text(
            "Gib deinen Spielernamen ein. Standardmäßig sucht SoulBuddy automatisch im lokalen Netzwerk. Optional kann Soullocke für die Synchronisierung verwendet werden.",
            15,
            FontWeight.Normal,
            "#94A3B8"));
        content.Children.Add(_activePlayerCard);

        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(CreateLabel("Spielername"));
        form.Children.Add(_playerNameBox);
        form.Children.Add(_soullockeCheckBox);
        form.Children.Add(CreateLabel("Soullocke-Link"));
        form.Children.Add(_soullockeLinkBox);
        form.Children.Add(CreateLabel("Soullocke-Passwort"));
        form.Children.Add(_soullockePasswordBox);
        form.Children.Add(Text(
            "Bei aktiviertem Soullocke-Sync werden Link und Passwort lokal im Spielerprofil gespeichert. Das lokale Netzwerk wird dann nicht verwendet.",
            11,
            FontWeight.Normal,
            "#7C8BA1"));
        form.Children.Add(CreateButton("Starten", StartAsync, true));
        form.Children.Add(_statusText);
        content.Children.Add(CreateCard(form));

        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private void UpdateSoullockeFieldState()
    {
        var enabled = _soullockeCheckBox.IsChecked == true;
        _soullockeLinkBox.IsEnabled = enabled;
        _soullockePasswordBox.IsEnabled = enabled;
        _soullockeLinkBox.Opacity = enabled ? 1 : 0.5;
        _soullockePasswordBox.Opacity = enabled ? 1 : 0.5;
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        await LoadActivePlayerAsync();
    }

    private async Task LoadActivePlayerAsync()
    {
        try
        {
            _activeContext = await _sessionStore.LoadActiveAsync();
            if (_activeContext is null)
            {
                _activePlayerCard.IsVisible = false;
                return;
            }

            _activePlayerTitle.Text = _activeContext.LocalPlayer.DisplayName;
            _playerNameBox.Text = _activeContext.LocalPlayer.DisplayName;
            _soullockeCheckBox.IsChecked = _activeContext.SoullockeEnabled;
            _soullockeLinkBox.Text = _activeContext.SoullockeLink;
            _soullockePasswordBox.Text = _activeContext.SoullockePassword;
            UpdateSoullockeFieldState();
            _activePlayerCard.IsVisible = true;
        }
        catch (Exception ex)
        {
            SetStatus($"Das lokale Spielerprofil konnte nicht geladen werden: {ex.Message}", true);
        }
    }

    private async Task StartAsync()
    {
        await ExecuteAsync(async () =>
        {
            var playerName = _playerNameBox.Text ?? string.Empty;
            var useSoullocke = _soullockeCheckBox.IsChecked == true;
            var link = _soullockeLinkBox.Text ?? string.Empty;
            var password = _soullockePasswordBox.Text ?? string.Empty;

            ValidateSoullockeInput(useSoullocke, link, password);

            SoullockeLaunchSettings.Configure(
                useSoullocke,
                link,
                password,
                playerName);

            var context = await _sessionStore.StartAsync(
                playerName,
                useSoullocke,
                link,
                password);
            OpenMainWindow(context);
        });
    }

    private async Task ContinueAsync()
    {
        if (_activeContext is null)
        {
            return;
        }

        await ExecuteAsync(() =>
        {
            ValidateSoullockeInput(
                _activeContext.SoullockeEnabled,
                _activeContext.SoullockeLink,
                _activeContext.SoullockePassword);

            SoullockeLaunchSettings.Configure(
                _activeContext.SoullockeEnabled,
                _activeContext.SoullockeLink,
                _activeContext.SoullockePassword,
                _activeContext.LocalPlayer.DisplayName);
            OpenMainWindow(_activeContext);
            return Task.CompletedTask;
        });
    }

    private static void ValidateSoullockeInput(
        bool enabled,
        string link,
        string password)
    {
        if (!enabled)
        {
            return;
        }

        _ = SoullockeLaunchSettings.ExtractSessionId(link);
        if (string.IsNullOrWhiteSpace(password))
        {
            throw new ArgumentException("Bitte gib das Soullocke-Passwort ein.");
        }
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        try
        {
            SetStatus("SoulBuddy wird gestartet …", false);
            await action();
        }
        catch (Exception ex)
        {
            SetStatus(ex.Message, true);
        }
    }

    private void OpenMainWindow(SessionContext context)
    {
        var mainWindow = new MainWindow(context);
        mainWindow.Show();
        Close();
    }

    private void SetStatus(string message, bool isError)
    {
        _statusText.Text = message;
        _statusText.Foreground = Brush(isError ? "#FCA5A5" : "#A7F3D0");
    }

    private static TextBox CreateTextBox(string placeholderText) => new()
    {
        PlaceholderText = placeholderText,
        FontSize = 15,
        Padding = new Thickness(13, 11),
        Background = Brush("#0F1829"),
        Foreground = Brush("#F8FAFC"),
        BorderBrush = Brush("#344763"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9)
    };

    private static TextBlock CreateLabel(string value) =>
        Text(value, 14, FontWeight.SemiBold, "#E2E8F0");

    private static Button CreateButton(string text, Func<Task> action, bool primary)
    {
        var button = new Button
        {
            Content = text,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            HorizontalContentAlignment = HorizontalAlignment.Center,
            Padding = new Thickness(14, 11),
            FontSize = 14,
            FontWeight = FontWeight.SemiBold,
            Background = Brush(primary ? "#2563EB" : "#172554"),
            Foreground = Brush("#F8FAFC"),
            BorderBrush = Brush(primary ? "#60A5FA" : "#334E8A"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(9)
        };
        button.Click += async (_, _) => await action();
        return button;
    }

    private static Border CreateCard(Control child) => new()
    {
        Background = Brush("#151F33"),
        BorderBrush = Brush("#2B3C58"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(15),
        Padding = new Thickness(22),
        Child = child
    };

    private static TextBlock Text(string value, double fontSize, FontWeight fontWeight, string color) => new()
    {
        Text = value,
        FontSize = fontSize,
        FontWeight = fontWeight,
        Foreground = Brush(color)
    };

    private static SolidColorBrush Brush(string color) => new(Color.Parse(color));
}
