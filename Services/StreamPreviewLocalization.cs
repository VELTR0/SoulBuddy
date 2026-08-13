using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

internal static class StreamPreviewLocalization
{
    private static readonly ConditionalWeakTable<TextBlock, object> WiredTexts = new();
    private static readonly HashSet<TextBlock> Applying = [];
    private static DispatcherTimer? _timer;

    private static readonly string[][] Phrases =
    [
        P("Nicht gestartet", "Not started", "Non démarré", "No iniciado", "Non avviato", "未開始"),
        P("Warte auf Videoframes", "Waiting for video frames", "En attente des images vidéo", "Esperando fotogramas", "In attesa dei frame video", "映像フレーム待機中"),
        P("Warte auf Partner-Stream", "Waiting for partner stream", "En attente du stream du partenaire", "Esperando el stream del compañero", "In attesa dello stream del compagno", "パートナーのストリーム待機中"),
        P("Kein Partnerbild", "No partner video", "Aucune image du partenaire", "Sin imagen del compañero", "Nessuna immagine del compagno", "パートナー映像なし")
    ];

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            LocalizationService.LanguageChanged += (_, _) => Dispatcher.UIThread.Post(ApplyToOpenWindows);
            _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
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
            foreach (var textBlock in window.GetVisualDescendants().OfType<TextBlock>())
            {
                if (IsKnownPhrase(textBlock.Text))
                    Wire(textBlock);
            }
        }
    }

    private static void Wire(TextBlock textBlock)
    {
        if (!WiredTexts.TryGetValue(textBlock, out _))
        {
            WiredTexts.Add(textBlock, new object());
            textBlock.PropertyChanged += OnTextChanged;
        }
        ApplyLanguage(textBlock);
    }

    private static void OnTextChanged(object? sender, AvaloniaPropertyChangedEventArgs args)
    {
        if (sender is TextBlock textBlock && args.Property == TextBlock.TextProperty)
            ApplyLanguage(textBlock);
    }

    private static void ApplyLanguage(TextBlock textBlock)
    {
        if (!Applying.Add(textBlock))
            return;

        try
        {
            var source = textBlock.Text;
            if (string.IsNullOrWhiteSpace(source))
                return;

            var translated = Translate(source);
            if (!string.Equals(source, translated, StringComparison.Ordinal))
                textBlock.SetCurrentValue(TextBlock.TextProperty, translated);
        }
        finally
        {
            Applying.Remove(textBlock);
        }
    }

    private static string Translate(string source)
    {
        var targetIndex = LanguageIndex(LocalizationService.CurrentLanguage);
        foreach (var phrase in Phrases)
        {
            if (phrase.Any(value => string.Equals(value, source, StringComparison.Ordinal)))
                return phrase[targetIndex];
        }
        return source;
    }

    private static bool IsKnownPhrase(string? value) =>
        !string.IsNullOrWhiteSpace(value) &&
        Phrases.Any(phrase => phrase.Any(item => string.Equals(item, value, StringComparison.Ordinal)));

    private static int LanguageIndex(AppLanguage language) => language switch
    {
        AppLanguage.English => 1,
        AppLanguage.French => 2,
        AppLanguage.Spanish => 3,
        AppLanguage.Italian => 4,
        AppLanguage.Japanese => 5,
        _ => 0
    };

    private static string[] P(string de, string en, string fr, string es, string it, string ja) =>
        [de, en, fr, es, it, ja];
}
