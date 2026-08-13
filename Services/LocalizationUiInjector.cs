using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Controls.Primitives;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

internal static class LocalizationUiInjector
{
    private static readonly ConditionalWeakTable<AvaloniaObject, ControlLocalizationState> States = new();
    private static readonly HashSet<MenuItem> WiredLanguageItems = [];
    private static DispatcherTimer? _timer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            LocalizationService.LanguageChanged += (_, _) => Dispatcher.UIThread.Post(ApplyToOpenWindows);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
            _timer.Tick += (_, _) => ApplyToOpenWindows();
            _timer.Start();
            ApplyToOpenWindows();
        });
    }

    private static void ApplyToOpenWindows()
    {
        if (Application.Current?.ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        foreach (var window in desktop.Windows)
        {
            WireLanguageMenu(window);
            ApplyWindow(window);
        }
    }

    private static void WireLanguageMenu(Window window)
    {
        foreach (var button in window.GetVisualDescendants().OfType<Button>())
        {
            if (button.Flyout is not MenuFlyout menu)
                continue;

            var items = menu.Items.OfType<MenuItem>().ToArray();
            if (!items.Any(item => (item.Header?.ToString() ?? string.Empty).Contains("🇩🇪", StringComparison.Ordinal)))
                continue;

            button.SetCurrentValue(ContentControl.ContentProperty, LocalizationService.CurrentFlag);

            foreach (var item in items)
            {
                if (!TryLanguageFromHeader(item.Header?.ToString(), out var language) ||
                    !WiredLanguageItems.Add(item))
                {
                    continue;
                }

                item.Click += (_, _) =>
                {
                    LocalizationService.SetLanguage(language);
                    button.SetCurrentValue(ContentControl.ContentProperty, LocalizationService.CurrentFlag);
                    ApplyToOpenWindows();
                };
            }
        }
    }

    private static bool TryLanguageFromHeader(string? header, out AppLanguage language)
    {
        var value = header ?? string.Empty;
        if (value.Contains("🇬🇧", StringComparison.Ordinal)) { language = AppLanguage.English; return true; }
        if (value.Contains("🇫🇷", StringComparison.Ordinal)) { language = AppLanguage.French; return true; }
        if (value.Contains("🇪🇸", StringComparison.Ordinal)) { language = AppLanguage.Spanish; return true; }
        if (value.Contains("🇮🇹", StringComparison.Ordinal)) { language = AppLanguage.Italian; return true; }
        if (value.Contains("🇯🇵", StringComparison.Ordinal)) { language = AppLanguage.Japanese; return true; }
        if (value.Contains("🇩🇪", StringComparison.Ordinal)) { language = AppLanguage.German; return true; }
        language = AppLanguage.German;
        return false;
    }

    private static void ApplyWindow(Window window)
    {
        foreach (var visual in window.GetVisualDescendants())
        {
            if (visual is TextBlock textBlock)
                ApplyText(textBlock, TextBlock.TextProperty, textBlock.Text, "text");

            if (visual is TextBox textBox)
                ApplyText(textBox, TextBox.PlaceholderTextProperty, textBox.PlaceholderText, "placeholder");

            if (visual is ContentControl contentControl && contentControl.Content is string content)
                ApplyContent(contentControl, content, "content");

            if (visual is HeaderedContentControl headered && headered.Header is string header)
                ApplyHeader(headered, header, "header");
        }
    }

    private static void ApplyText(
        AvaloniaObject owner,
        StyledProperty<string?> property,
        string? current,
        string propertyKey)
    {
        if (string.IsNullOrEmpty(current))
            return;

        var source = ResolveSource(owner, propertyKey, current);
        var translated = LocalizationService.Ui(source);
        var state = GetState(owner, propertyKey);
        state.LastApplied = translated;
        if (!string.Equals(current, translated, StringComparison.Ordinal))
            owner.SetCurrentValue(property, translated);
    }

    private static void ApplyContent(ContentControl owner, string current, string propertyKey)
    {
        if (string.IsNullOrEmpty(current) || IsLanguageFlag(current))
            return;

        var source = ResolveSource(owner, propertyKey, current);
        var translated = LocalizationService.Ui(source);
        var state = GetState(owner, propertyKey);
        state.LastApplied = translated;
        if (!string.Equals(current, translated, StringComparison.Ordinal))
            owner.SetCurrentValue(ContentControl.ContentProperty, translated);
    }

    private static void ApplyHeader(HeaderedContentControl owner, string current, string propertyKey)
    {
        if (string.IsNullOrEmpty(current) || IsLanguageMenuHeader(current))
            return;

        var source = ResolveSource(owner, propertyKey, current);
        var translated = LocalizationService.Ui(source);
        var state = GetState(owner, propertyKey);
        state.LastApplied = translated;
        if (!string.Equals(current, translated, StringComparison.Ordinal))
            owner.SetCurrentValue(HeaderedContentControl.HeaderProperty, translated);
    }

    private static string ResolveSource(AvaloniaObject owner, string propertyKey, string current)
    {
        var state = GetState(owner, propertyKey);
        if (state.Source is null)
        {
            state.Source = current;
            return current;
        }

        if (state.LastApplied is not null && string.Equals(current, state.LastApplied, StringComparison.Ordinal))
            return state.Source;

        state.Source = current;
        return current;
    }

    private static TextLocalizationState GetState(AvaloniaObject owner, string propertyKey)
    {
        var state = States.GetValue(owner, _ => new ControlLocalizationState());
        if (!state.Properties.TryGetValue(propertyKey, out var propertyState))
        {
            propertyState = new TextLocalizationState();
            state.Properties[propertyKey] = propertyState;
        }
        return propertyState;
    }

    private static bool IsLanguageFlag(string value) =>
        value is "🇩🇪" or "🇬🇧" or "🇫🇷" or "🇪🇸" or "🇮🇹" or "🇯🇵";

    private static bool IsLanguageMenuHeader(string value) =>
        value.Contains("🇩🇪", StringComparison.Ordinal) ||
        value.Contains("🇬🇧", StringComparison.Ordinal) ||
        value.Contains("🇫🇷", StringComparison.Ordinal) ||
        value.Contains("🇪🇸", StringComparison.Ordinal) ||
        value.Contains("🇮🇹", StringComparison.Ordinal) ||
        value.Contains("🇯🇵", StringComparison.Ordinal);

    private sealed class ControlLocalizationState
    {
        public Dictionary<string, TextLocalizationState> Properties { get; } = new(StringComparer.Ordinal);
    }

    private sealed class TextLocalizationState
    {
        public string? Source { get; set; }
        public string? LastApplied { get; set; }
    }
}
