using System.Runtime.CompilerServices;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

internal static class TrackerSetupTextUi
{
    private static DispatcherTimer? _timer;

    private static readonly string[] TrackerLink =
    [
        "Tracker-Website-Link",
        "Tracker Website Link",
        "Lien du site du tracker",
        "Enlace del sitio del tracker",
        "Link al sito del tracker",
        "トラッカーサイトリンク"
    ];

    private static readonly string[] Password =
    [
        "Passwort",
        "Password",
        "Mot de passe",
        "Contraseña",
        "Password",
        "パスワード"
    ];

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            LocalizationService.LanguageChanged += (_, _) => Dispatcher.UIThread.Post(Apply);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => Apply();
            _timer.Start();
            Apply();
        });
    }

    private static void Apply()
    {
        if (Avalonia.Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        foreach (var window in desktop.Windows)
        {
            if (!string.Equals(window.GetType().Name, "SessionSetupWindow", StringComparison.Ordinal))
                continue;

            foreach (var text in window.GetVisualDescendants().OfType<TextBlock>())
            {
                if (Matches(text.Text, TrackerLink))
                    text.Text = Current(TrackerLink);
                else if (Matches(text.Text, Password))
                    text.Text = Current(Password);
            }

            foreach (var box in window.GetVisualDescendants().OfType<TextBox>())
            {
                if (Matches(box.PlaceholderText, TrackerLink))
                    box.PlaceholderText = Current(TrackerLink);
                else if (Matches(box.PlaceholderText, Password))
                    box.PlaceholderText = Current(Password);
            }
        }
    }

    private static bool Matches(string? value, IReadOnlyList<string> translations) =>
        value is not null && translations.Any(candidate =>
            string.Equals(candidate, value, StringComparison.Ordinal));

    private static string Current(IReadOnlyList<string> translations) =>
        translations[LocalizationService.CurrentLanguage switch
        {
            AppLanguage.English => 1,
            AppLanguage.French => 2,
            AppLanguage.Spanish => 3,
            AppLanguage.Italian => 4,
            AppLanguage.Japanese => 5,
            _ => 0
        }];
}
