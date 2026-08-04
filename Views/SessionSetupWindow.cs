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
    private readonly TextBox _sessionIdBox;
    private readonly TextBox _playerNameBox;
    private readonly TextBlock _statusText;
    private readonly Border _activeSessionCard;
    private readonly TextBlock _activeSessionTitle;
    private readonly TextBlock _activeSessionDetails;
    private SessionContext? _activeContext;

    public SessionSetupWindow()
    {
        Title = "SoulBuddy · Session";
        Width = 760;
        Height = 650;
        MinWidth = 640;
        MinHeight = 560;
        Background = Brush("#0B1220");

        _sessionIdBox = CreateTextBox("z. B. meine-soullink-session");
        _playerNameBox = CreateTextBox("Dein Spielername");
        _statusText = Text("", 13, FontWeight.Medium, "#CBD5E1");
        _statusText.TextWrapping = TextWrapping.Wrap;

        _activeSessionTitle = Text("", 20, FontWeight.Bold, "#F8FAFC");
        _activeSessionDetails = Text("", 14, FontWeight.Normal, "#CBD5E1");
        _activeSessionDetails.TextWrapping = TextWrapping.Wrap;

        _activeSessionCard = CreateCard(new StackPanel
        {
            Spacing = 10,
            Children =
            {
                Text("Zuletzt aktive Session", 12, FontWeight.Bold, "#93C5FD"),
                _activeSessionTitle,
                _activeSessionDetails,
                CreateButton("Session fortsetzen", ContinueActiveSessionAsync, true)
            }
        });
        _activeSessionCard.IsVisible = false;

        Content = BuildLayout();
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
            "Erstelle eine neue SoulLink-Session oder tritt mit einer frei gewählten Session-ID bei.",
            15,
            FontWeight.Normal,
            "#94A3B8"));
        content.Children.Add(_activeSessionCard);

        var form = new StackPanel { Spacing = 12 };
        form.Children.Add(CreateLabel("Session-ID"));
        form.Children.Add(_sessionIdBox);
        form.Children.Add(Text(
            "Groß-/Kleinschreibung wird ignoriert. Leerzeichen und Sonderzeichen werden automatisch in Bindestriche umgewandelt.",
            12,
            FontWeight.Normal,
            "#7C8BA1"));
        form.Children.Add(CreateLabel("Spielername"));
        form.Children.Add(_playerNameBox);

        var buttonGrid = new Grid
        {
            ColumnDefinitions = new ColumnDefinitions("*,*"),
            ColumnSpacing = 12,
            Margin = new Thickness(0, 6, 0, 0)
        };
        buttonGrid.Children.Add(CreateButton("Session erstellen", CreateSessionAsync, true));
        var joinButton = CreateButton("Session beitreten", JoinSessionAsync, false);
        Grid.SetColumn(joinButton, 1);
        buttonGrid.Children.Add(joinButton);
        form.Children.Add(buttonGrid);
        form.Children.Add(_statusText);

        content.Children.Add(CreateCard(form));

        return new ScrollViewer
        {
            Content = content,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto
        };
    }

    private async void OnOpened(object? sender, EventArgs eventArgs)
    {
        await LoadActiveSessionAsync();
    }

    private async Task LoadActiveSessionAsync()
    {
        try
        {
            _activeContext = await _sessionStore.LoadActiveAsync();
            if (_activeContext is null)
            {
                _activeSessionCard.IsVisible = false;
                return;
            }

            _activeSessionTitle.Text = $"Session {_activeContext.Session.Id}";
            _activeSessionDetails.Text =
                $"Spieler: {_activeContext.LocalPlayer.DisplayName} · Slot {_activeContext.LocalPlayer.Slot}\n" +
                $"Teilnehmer: {_activeContext.Session.Players.Count}/2";
            _activeSessionCard.IsVisible = true;
        }
        catch (Exception ex)
        {
            SetStatus($"Die aktive Session konnte nicht geladen werden: {ex.Message}", true);
        }
    }

    private async Task CreateSessionAsync()
    {
        await ExecuteAsync(async () =>
        {
            var context = await _sessionStore.CreateAsync(
                _sessionIdBox.Text ?? string.Empty,
                _playerNameBox.Text ?? string.Empty);
            OpenMainWindow(context);
        });
    }

    private async Task JoinSessionAsync()
    {
        await ExecuteAsync(async () =>
        {
            var context = await _sessionStore.JoinAsync(
                _sessionIdBox.Text ?? string.Empty,
                _playerNameBox.Text ?? string.Empty);
            OpenMainWindow(context);
        });
    }

    private Task ContinueActiveSessionAsync()
    {
        if (_activeContext is not null)
        {
            OpenMainWindow(_activeContext);
        }

        return Task.CompletedTask;
    }

    private async Task ExecuteAsync(Func<Task> action)
    {
        try
        {
            SetStatus("Session wird gespeichert …", false);
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

    private static TextBox CreateTextBox(string placeholderText)
    {
        return new TextBox
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
    }

    private static TextBlock CreateLabel(string value) =>
        Text(value, 14, FontWeight.SemiBold, "#E2E8F0");

    private static Button CreateButton(
        string text,
        Func<Task> action,
        bool primary)
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

    private static Border CreateCard(Control child)
    {
        return new Border
        {
            Background = Brush("#151F33"),
            BorderBrush = Brush("#2B3C58"),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(15),
            Padding = new Thickness(22),
            Child = child
        };
    }

    private static TextBlock Text(
        string value,
        double fontSize,
        FontWeight fontWeight,
        string color)
    {
        return new TextBlock
        {
            Text = value,
            FontSize = fontSize,
            FontWeight = fontWeight,
            Foreground = Brush(color)
        };
    }

    private static SolidColorBrush Brush(string color) =>
        new(Color.Parse(color));
}
