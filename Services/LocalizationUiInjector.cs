using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

internal static class LocalizationUiInjector
{
    private static readonly ConditionalWeakTable<AvaloniaObject, ControlLocalizationState> States = new();
    private static readonly HashSet<MenuItem> WiredLanguageItems = [];
    private static DispatcherTimer? _timer;

    private static readonly string[][] LiveExactPhrases =
    [
        P("Streaming", "Streaming", "Streaming", "Streaming", "Streaming", "ストリーミング"),
        P("Du", "You", "Toi", "Tú", "Tu", "あなた"),
        P("Partner", "Partner", "Partenaire", "Compañero", "Compagno", "パートナー"),
        P("Stream starten", "Start stream", "Démarrer le stream", "Iniciar stream", "Avvia stream", "ストリーム開始"),
        P("Stream stoppen", "Stop stream", "Arrêter le stream", "Detener stream", "Ferma stream", "ストリーム停止"),
        P("Stream in DeSmuMe anzeigen", "Show stream in DeSmuMe", "Afficher le stream dans DeSmuMe", "Mostrar stream en DeSmuMe", "Mostra stream in DeSmuMe", "DeSmuMeにストリームを表示"),
        P("Streams hier anzeigen", "Show streams here", "Afficher les streams ici", "Mostrar streams aquí", "Mostra gli stream qui", "ここにストリームを表示"),
        P("Nicht gestartet", "Not started", "Non démarré", "No iniciado", "Non avviato", "未開始"),
        P("Warte auf Videoframes", "Waiting for video frames", "En attente des images vidéo", "Esperando fotogramas", "In attesa dei frame video", "映像フレーム待機中"),
        P("Warte auf Partner-Stream", "Waiting for partner stream", "En attente du stream du partenaire", "Esperando el stream del compañero", "In attesa dello stream del compagno", "パートナーのストリーム待機中"),
        P("Kein Partnerbild", "No partner video", "Aucune image du partenaire", "Sin imagen del compañero", "Nessuna immagine del compagno", "パートナー映像なし"),
        P("Partner-Aktivität wird geladen …", "Loading partner activity …", "Chargement de l’activité du partenaire …", "Cargando actividad del compañero …", "Caricamento attività del compagno …", "パートナーのアクティビティを読み込み中…")
    ];

    private static readonly string[][] LiveFragmentPhrases =
    [
        P("Trainerkampf", "Trainer battle", "Combat de Dresseur", "Combate de Entrenador", "Lotta con Allenatore", "トレーナー戦"),
        P("Wilder Kampf", "Wild battle", "Combat sauvage", "Combate salvaje", "Lotta con Pokémon selvatico", "野生ポケモン戦"),
        P("Kampf erkannt", "Battle detected", "Combat détecté", "Combate detectado", "Lotta rilevata", "バトルを検出"),
        P("Aufenthaltsort wird ermittelt", "Detecting location", "Détection du lieu", "Detectando ubicación", "Rilevamento posizione", "場所を検出中"),
        P("Erkundet gerade die Welt", "Exploring the world", "Explore le monde", "Explorando el mundo", "Esplorazione del mondo", "フィールドを探索中"),
        P("Gegner: wird ermittelt …", "Opponent: detecting …", "Adversaire : détection …", "Rival: detectando …", "Avversario: rilevamento …", "相手: 検出中…"),
        P(" KP", " HP", " PV", " PS", " PS", " HP")
    ];

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
        var translated = TranslateUi(source);
        var state = GetState(owner, propertyKey);
        state.LastApplied = translated;
        if (!string.Equals(current, translated, StringComparison.Ordinal))
            owner.SetCurrentValue(property, translated);
    }

    private static void ApplyContent(ContentControl owner, string current, string propertyKey)
    {
        if (string.IsNullOrEmpty(current) || IsLanguageFlag(current) || IsPendingSessionCopyButton(owner, current))
            return;

        var source = ResolveSource(owner, propertyKey, current);
        var translated = TranslateUi(source);
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
        var translated = TranslateUi(source);
        var state = GetState(owner, propertyKey);
        state.LastApplied = translated;
        if (!string.Equals(current, translated, StringComparison.Ordinal))
            owner.SetCurrentValue(HeaderedContentControl.HeaderProperty, translated);
    }

    private static string TranslateUi(string source)
    {
        var translated = LocalizationService.Ui(source);
        if (LocalizationService.CurrentLanguage == AppLanguage.German)
            return translated;

        var exact = LiveExactPhrases.FirstOrDefault(phrase =>
            phrase.Any(value => string.Equals(value, translated, StringComparison.Ordinal)) ||
            string.Equals(phrase[0], source, StringComparison.Ordinal));
        if (exact is not null)
            translated = GetLiveTranslation(exact);

        foreach (var phrase in LiveFragmentPhrases.OrderByDescending(item => item[0].Length))
        {
            if (translated.Contains(phrase[0], StringComparison.Ordinal))
                translated = translated.Replace(
                    phrase[0],
                    GetLiveTranslation(phrase),
                    StringComparison.Ordinal);
        }

        return translated;
    }

    private static string GetLiveTranslation(string[] translations) =>
        translations[LocalizationService.CurrentLanguage switch
        {
            AppLanguage.English => 1,
            AppLanguage.French => 2,
            AppLanguage.Spanish => 3,
            AppLanguage.Italian => 4,
            AppLanguage.Japanese => 5,
            _ => 0
        }];

    private static string[] P(string de, string en, string fr, string es, string it, string ja) =>
        [de, en, fr, es, it, ja];

    private static bool IsPendingSessionCopyButton(ContentControl owner, string current)
    {
        if (owner is not Button || !LocalizationService.IsTranslationOf(current, "Kopieren"))
            return false;

        return owner.Parent is Grid grid && grid.Children
            .OfType<TextBlock>()
            .Any(text => string.Equals(text.Text, "Session Link:", StringComparison.Ordinal));
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
