using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Media;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

/// <summary>
/// Keeps the Live activity UI local to the language selected on this client and
/// gives the local activity the same card treatment as the partner activity.
///
/// Live state is intentionally transported independently from the UI language.
/// This guard also accepts already-localized partner text so a client can never
/// force its language onto another client.
/// </summary>
internal static class LiveActivityUiPolish
{
    private static readonly ConditionalWeakTable<TextBlock, object> WiredTexts = new();
    private static readonly ConditionalWeakTable<StackPanel, object> WrappedLivePanels = new();
    private static readonly HashSet<TextBlock> ApplyingTranslation = [];
    private static DispatcherTimer? _timer;

    private static readonly string[][] ActivityPhrases =
    [
        P("Warte auf Live-Daten aus dem Emulator …", "Waiting for live data from the emulator …", "En attente des données en direct de l’émulateur …", "Esperando datos en vivo del emulador …", "In attesa dei dati live dall’emulatore …", "エミュレーターのライブデータを待機中…"),
        P("Gegner: wird ermittelt …", "Opponent: detecting …", "Adversaire : détection …", "Rival: detectando …", "Avversario: rilevamento …", "相手: 検出中…"),
        P("Aufenthaltsort wird ermittelt", "Detecting location", "Détection du lieu", "Detectando ubicación", "Rilevamento posizione", "場所を検出中"),
        P("Erkundet gerade die Welt", "Exploring the world", "Explore le monde", "Explorando el mundo", "Esplorazione del mondo", "フィールドを探索中"),
        P("Partner-Aktivität wird geladen …", "Loading partner activity …", "Chargement de l’activité du partenaire …", "Cargando actividad del compañero …", "Caricamento attività del compagno …", "パートナーのアクティビティを読み込み中…"),
        P("LIVE ENCOUNTER", "LIVE ENCOUNTER", "RENCONTRE EN DIRECT", "ENCUENTRO EN VIVO", "INCONTRO LIVE", "ライブエンカウント"),
        P("LIVE-STATUS", "LIVE STATUS", "STATUT EN DIRECT", "ESTADO EN VIVO", "STATO LIVE", "ライブステータス"),
        P("Trainerkampf", "Trainer battle", "Combat de Dresseur", "Combate de Entrenador", "Lotta con Allenatore", "トレーナー戦"),
        P("Wilder Kampf", "Wild battle", "Combat sauvage", "Combate salvaje", "Lotta con Pokémon selvatico", "野生ポケモン戦"),
        P("Kampf erkannt", "Battle detected", "Combat détecté", "Combate detectado", "Lotta rilevata", "バトルを検出"),
        P("Unbekannter Ort (", "Unknown location (", "Lieu inconnu (", "Ubicación desconocida (", "Luogo sconosciuto (", "不明な場所 ("),
        P("Gegner: ", "Opponent: ", "Adversaire : ", "Rival: ", "Avversario: ", "相手: "),
        P("Aktiv: ", "Active: ", "Actif : ", "Activo: ", "Attivo: ", "使用中: "),
        P(" KP", " HP", " PV", " PS", " PS", " HP")
    ];

    private static readonly string[] YouLabels =
        ["Du", "You", "Toi", "Tú", "Tu", "あなた"];

    private static readonly string[] PartnerLabels =
        ["Partner", "Partenaire", "Compañero", "Compagno", "パートナー"];

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromMilliseconds(200)
            };
            _timer.Tick += (_, _) => PolishOpenWindows();
            _timer.Start();
            PolishOpenWindows();
        });
    }

    private static void PolishOpenWindows()
    {
        if (Application.Current?.ApplicationLifetime is not
            IClassicDesktopStyleApplicationLifetime desktop)
        {
            return;
        }

        foreach (var window in desktop.Windows)
        {
            var livePanel = FindLivePanel(window);
            if (livePanel is null)
                continue;

            TryWrapLocalActivity(livePanel);
            WireActivityCards(livePanel);
        }
    }

    private static StackPanel? FindLivePanel(Window window)
    {
        var tabs = window.GetVisualDescendants().OfType<TabControl>();
        foreach (var tabControl in tabs)
        {
            var liveTab = tabControl.Items
                .OfType<TabItem>()
                .FirstOrDefault(item =>
                    LocalizationService.IsTranslationOf(
                        item.Header?.ToString(),
                        "Live"));

            if ((liveTab?.Content as ScrollViewer)?.Content is StackPanel panel)
                return panel;
        }

        return null;
    }

    private static void TryWrapLocalActivity(StackPanel livePanel)
    {
        if (WrappedLivePanels.TryGetValue(livePanel, out _))
            return;

        // StreamUiInjector adds the local "Du" label before the two original
        // bound Live text blocks. Wait for that shape so we can move the same
        // controls into a card without replacing their bindings.
        if (livePanel.Children.Count < 3 ||
            livePanel.Children[0] is not TextBlock youLabel ||
            !IsAnyLabel(youLabel.Text, YouLabels) ||
            livePanel.Children[1] is not TextBlock liveTitle ||
            livePanel.Children[2] is not TextBlock liveText)
        {
            return;
        }

        livePanel.Children.RemoveAt(2);
        livePanel.Children.RemoveAt(1);
        livePanel.Children.RemoveAt(0);

        var panel = new StackPanel { Spacing = 6 };
        panel.Children.Add(youLabel);
        panel.Children.Add(liveTitle);
        panel.Children.Add(liveText);

        livePanel.Children.Insert(0, Card(panel));
        WrappedLivePanels.Add(livePanel, new object());

        WireActivityText(liveTitle);
        WireActivityText(liveText);
    }

    private static void WireActivityCards(StackPanel livePanel)
    {
        foreach (var border in livePanel.Children.OfType<Border>())
        {
            if (border.Child is not StackPanel panel)
                continue;

            var texts = panel.Children.OfType<TextBlock>().ToArray();
            if (texts.Length < 3)
                continue;

            var label = texts[0].Text;
            if (!IsAnyLabel(label, YouLabels) &&
                !IsAnyLabel(label, PartnerLabels))
            {
                continue;
            }

            WireActivityText(texts[1]);
            WireActivityText(texts[2]);
        }
    }

    private static void WireActivityText(TextBlock textBlock)
    {
        if (WiredTexts.TryGetValue(textBlock, out _))
            return;

        WiredTexts.Add(textBlock, new object());
        textBlock.PropertyChanged += OnActivityTextPropertyChanged;
        ApplyClientLanguage(textBlock);
    }

    private static void OnActivityTextPropertyChanged(
        object? sender,
        AvaloniaPropertyChangedEventArgs eventArgs)
    {
        if (sender is TextBlock textBlock &&
            eventArgs.Property == TextBlock.TextProperty)
        {
            ApplyClientLanguage(textBlock);
        }
    }

    private static void ApplyClientLanguage(TextBlock textBlock)
    {
        if (!ApplyingTranslation.Add(textBlock))
            return;

        try
        {
            var source = textBlock.Text;
            if (string.IsNullOrWhiteSpace(source))
                return;

            var translated = TranslateActivity(source);
            if (!string.Equals(source, translated, StringComparison.Ordinal))
                textBlock.SetCurrentValue(TextBlock.TextProperty, translated);
        }
        finally
        {
            ApplyingTranslation.Remove(textBlock);
        }
    }

    private static string TranslateActivity(string source)
    {
        // LocalizationService handles Pokémon names and the application's normal
        // German source strings. The second pass below deliberately recognizes
        // every supported language, so received partner activity is normalized
        // to this client's language even when the sender already localized it.
        var translated = LocalizationService.Ui(source);
        var targetIndex = LanguageIndex(LocalizationService.CurrentLanguage);

        foreach (var phrase in ActivityPhrases)
        {
            var target = phrase[targetIndex];
            foreach (var variant in phrase.OrderByDescending(value => value.Length))
            {
                if (translated.Contains(variant, StringComparison.Ordinal))
                    translated = translated.Replace(variant, target, StringComparison.Ordinal);
            }
        }

        return translated;
    }

    private static int LanguageIndex(AppLanguage language) => language switch
    {
        AppLanguage.English => 1,
        AppLanguage.French => 2,
        AppLanguage.Spanish => 3,
        AppLanguage.Italian => 4,
        AppLanguage.Japanese => 5,
        _ => 0
    };

    private static bool IsAnyLabel(string? value, IEnumerable<string> labels) =>
        !string.IsNullOrWhiteSpace(value) &&
        labels.Any(label => string.Equals(label, value, StringComparison.Ordinal));

    private static string[] P(
        string de,
        string en,
        string fr,
        string es,
        string it,
        string ja) =>
        [de, en, fr, es, it, ja];

    private static Border Card(Control child) => new()
    {
        Background = Brush("#151F33"),
        BorderBrush = Brush("#2B3C58"),
        BorderThickness = new Thickness(1),
        CornerRadius = new CornerRadius(9),
        Padding = new Thickness(10),
        Child = child
    };

    private static SolidColorBrush Brush(string color) =>
        new(Color.Parse(color));
}
