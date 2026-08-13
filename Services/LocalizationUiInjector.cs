using System.Net.Http;
using System.Runtime.CompilerServices;
using System.Text;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.VisualTree;

namespace SoulBuddy.Services;

internal static class LocalizationUiInjector
{
    private const string LocationNamesUrl =
        "https://raw.githubusercontent.com/PokeAPI/pokeapi/master/data/v2/csv/location_names.csv";

    private static readonly ConditionalWeakTable<AvaloniaObject, ControlLocalizationState> States = new();
    private static readonly HashSet<MenuItem> WiredLanguageItems = [];
    private static readonly object LocationSync = new();
    private static readonly HttpClient LocationHttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<int, Dictionary<string, string>> LocationNames = [];
    private static readonly Dictionary<string, int> LocationIdsByName =
        new(StringComparer.OrdinalIgnoreCase);
    private static bool _locationDownloadStarted;
    private static DispatcherTimer? _timer;

    private static readonly Dictionary<string, string> LocationAliases =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Finsterhöhle"] = "Dunkelhöhle",
            ["Knofensaturm"] = "Knofensa-Turm",
            ["Einheitshöhle"] = "Einheitstunnel",
            ["Digdas Höhle"] = "Digda-Höhle",
            ["Ruinen von Teak City"] = "Turmruine",
            ["Rocket-Versteck"] = "Team Rocket-Hauptquartier",
            ["Dukatia-Tunnel"] = "Dukatia-Passage",
            ["Silberberghöhle"] = "Silberberg (Höhle)",
            ["Pokéathlon-Hallen"] = "Pokéathlon",
            ["Safari-Zonen-Eingang"] = "Safari-Eingang",
            ["Felsenhöhle"] = "Felsschlundhöhle",
            ["Zugang zur Kampfzone"] = "Kampfzonenzugang",
            ["Erholungsgebiet"] = "Erholungsareal",
            ["Feuriohütte"] = "Feurio-Hütte",
            ["Frühlingspfad"] = "Quellenpfad",
            ["Fernes Land"] = "Entferntes Land",
            ["Reisender Mann"] = "Reisender",
            ["Pensionsleiter"] = "Pensions-Paar"
        };

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
        P("Partner-Aktivität wird geladen …", "Loading partner activity …", "Chargement de l’activité du partenaire …", "Cargando actividad del compañero …", "Caricamento attività del compagno …", "パートナーのアクティビティを読み込み中…"),
        P("VERKNÜPFT MIT", "LINKED WITH", "LIÉ À", "VINCULADO CON", "COLLEGATO A", "リンク先"),
        P("KAMPFUNFÄHIG", "FAINTED", "K.O.", "DEBILITADO", "KO", "ひんし"),
        P("Noch nicht\nverknüpft", "Not linked\nyet", "Pas encore\nlié", "Aún no\nvinculado", "Non ancora\ncollegato", "未リンク")
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
        TryLoadLocationNames(GetLocationCachePath());
        StartLocationNameDownloadIfNeeded();

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

        if (LocalizationService.CurrentLanguage != AppLanguage.German)
        {
            var exact = LiveExactPhrases.FirstOrDefault(phrase =>
                phrase.Any(value => string.Equals(value, translated, StringComparison.Ordinal)) ||
                string.Equals(phrase[0], source, StringComparison.Ordinal));
            if (exact is not null)
                translated = GetLiveTranslation(exact);

            foreach (var phrase in LiveFragmentPhrases.OrderByDescending(item => item[0].Length))
            {
                if (translated.Contains(phrase[0], StringComparison.Ordinal))
                {
                    translated = translated.Replace(
                        phrase[0],
                        GetLiveTranslation(phrase),
                        StringComparison.Ordinal);
                }
            }
        }

        return TranslateLocations(translated);
    }

    private static string TranslateLocations(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return source;

        lock (LocationSync)
        {
            if (LocationIdsByName.Count == 0)
                return source;
        }

        var separator = source.Contains("\r\n", StringComparison.Ordinal) ? "\r\n" : "\n";
        var lines = source.Replace("\r\n", "\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
            lines[index] = TranslateLocationLine(lines[index]);
        return string.Join(separator, lines);
    }

    private static string TranslateLocationLine(string line)
    {
        var trimmed = line.Trim();
        if (TryTranslateLocationName(trimmed, out var exact))
            return ReplaceTrimmedValue(line, trimmed, exact);

        const string pinMarker = "📍 ";
        var pinIndex = line.IndexOf(pinMarker, StringComparison.Ordinal);
        if (pinIndex >= 0)
        {
            var valueStart = pinIndex + pinMarker.Length;
            var candidate = line[valueStart..].Trim();
            if (TryTranslateLocationName(candidate, out var translated))
                return line[..valueStart] + translated;
        }

        var separatorIndex = line.LastIndexOf(": ", StringComparison.Ordinal);
        if (separatorIndex >= 0)
        {
            var valueStart = separatorIndex + 2;
            var candidate = line[valueStart..].Trim();
            if (TryTranslateLocationName(candidate, out var translated))
                return line[..valueStart] + translated;
        }

        return line;
    }

    private static string ReplaceTrimmedValue(string line, string trimmed, string replacement)
    {
        if (trimmed.Length == 0)
            return line;

        var index = line.IndexOf(trimmed, StringComparison.Ordinal);
        return index < 0
            ? replacement
            : line[..index] + replacement + line[(index + trimmed.Length)..];
    }

    private static bool TryTranslateLocationName(string source, out string translated)
    {
        translated = source;
        if (string.IsNullOrWhiteSpace(source))
            return false;

        var lookup = LocationAliases.TryGetValue(source.Trim(), out var alias)
            ? alias
            : source.Trim();
        var languageCode = LocationLanguageCode(LocalizationService.CurrentLanguage);

        lock (LocationSync)
        {
            if (!LocationIdsByName.TryGetValue(lookup, out var locationId) ||
                !LocationNames.TryGetValue(locationId, out var names) ||
                !names.TryGetValue(languageCode, out var target) ||
                string.IsNullOrWhiteSpace(target))
            {
                return false;
            }

            translated = target;
            return !string.Equals(source, target, StringComparison.Ordinal);
        }
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

    private static string LocationLanguageCode(AppLanguage language) => language switch
    {
        AppLanguage.English => "en",
        AppLanguage.French => "fr",
        AppLanguage.Spanish => "es",
        AppLanguage.Italian => "it",
        AppLanguage.Japanese => "ja",
        _ => "de"
    };

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

    private static void StartLocationNameDownloadIfNeeded()
    {
        lock (LocationSync)
        {
            if (LocationNames.Count >= 100 || _locationDownloadStarted)
                return;
            _locationDownloadStarted = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var csv = await LocationHttpClient.GetStringAsync(LocationNamesUrl);
                var path = GetLocationCachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, csv, new UTF8Encoding(false));
                LoadLocationNamesCsv(csv);
                Dispatcher.UIThread.Post(ApplyToOpenWindows);
            }
            catch
            {
                // Location localization is optional; canonical game names remain usable.
            }
        });
    }

    private static void TryLoadLocationNames(string path)
    {
        try
        {
            if (File.Exists(path))
                LoadLocationNamesCsv(File.ReadAllText(path));
        }
        catch
        {
            // A missing or malformed cache simply falls back to canonical names.
        }
    }

    private static void LoadLocationNamesCsv(string csv)
    {
        var byId = new Dictionary<int, Dictionary<string, string>>();

        foreach (var line in csv.Split('\n').Skip(1))
        {
            var fields = line.TrimEnd('\r').Split(',', 4);
            if (fields.Length < 3 ||
                !int.TryParse(fields[0], out var locationId) ||
                !int.TryParse(fields[1], out var languageId))
            {
                continue;
            }

            var language = languageId switch
            {
                1 => "ja",
                5 => "fr",
                6 => "de",
                7 => "es",
                8 => "it",
                9 => "en",
                _ => string.Empty
            };
            if (language.Length == 0)
                continue;

            var name = fields[2].Trim().Trim('"').Replace("\"\"", "\"", StringComparison.Ordinal);
            if (string.IsNullOrWhiteSpace(name))
                continue;

            if (!byId.TryGetValue(locationId, out var names))
            {
                names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                byId[locationId] = names;
            }
            names[language] = name;
        }

        if (byId.Count < 100)
            return;

        lock (LocationSync)
        {
            LocationNames.Clear();
            LocationIdsByName.Clear();

            foreach (var pair in byId)
            {
                LocationNames[pair.Key] = pair.Value;
                foreach (var name in pair.Value.Values)
                {
                    if (!string.IsNullOrWhiteSpace(name))
                        LocationIdsByName[name] = pair.Key;
                }
            }

            foreach (var alias in LocationAliases)
            {
                if (LocationIdsByName.TryGetValue(alias.Value, out var id))
                    LocationIdsByName[alias.Key] = id;
            }
        }
    }

    private static string GetLocationCachePath() =>
        Path.Combine(AppContext.BaseDirectory, "data", "location-names.csv");

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
