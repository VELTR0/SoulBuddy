using System.Net.Http;
using System.Text;
using System.Text.RegularExpressions;

namespace SoulBuddy.Services;

public enum AppLanguage
{
    German,
    English,
    French,
    Spanish,
    Italian,
    Japanese
}

public static class LocalizationService
{
    private const string PokemonNamesUrl = "https://raw.githubusercontent.com/PokeAPI/pokeapi/master/data/v2/csv/pokemon_species_names.csv";
    private static readonly object Sync = new();
    private static readonly HttpClient HttpClient = new() { Timeout = TimeSpan.FromSeconds(15) };
    private static readonly Dictionary<int, Dictionary<string, string>> PokemonNames = [];
    private static readonly Dictionary<string, int> PokemonIdsByLocalizedName = new(StringComparer.OrdinalIgnoreCase);
    private static Regex? _pokemonNameRegex;
    private static AppLanguage _currentLanguage = LoadSavedLanguage();
    private static bool _pokemonDownloadStarted;

    private static readonly string[][] UiPhrases =
    [
        P("AKTIVE SESSION", "ACTIVE SESSION", "SESSION ACTIVE", "SESIÓN ACTIVA", "SESSIONE ATTIVA", "アクティブセッション"),
        P("MITTSPIELER", "PARTNER", "PARTENAIRE", "COMPAÑERO", "COMPAGNO", "パートナー"),
        P("Aktuelles Team", "Current Team", "Équipe actuelle", "Equipo actual", "Squadra attuale", "現在のチーム"),
        P("Encounters", "Encounters", "Rencontres", "Encuentros", "Incontri", "エンカウント"),
        P("Details", "Details", "Détails", "Detalles", "Dettagli", "詳細"),
        P("Live", "Live", "Direct", "En vivo", "Live", "ライブ"),
        P("Stream", "Stream", "Stream", "Stream", "Stream", "ストリーム"),
        P("Lokales Streaming", "Local Streaming", "Streaming local", "Streaming local", "Streaming locale", "ローカルストリーミング"),
        P("Stream ansehen", "Watch stream", "Regarder le stream", "Ver stream", "Guarda stream", "ストリームを見る"),
        P("Eigenen oberen Bildschirm streamen", "Stream my top screen", "Diffuser mon écran supérieur", "Transmitir mi pantalla superior", "Trasmetti il mio schermo superiore", "上画面を配信"),
        P("Der empfangene Stream erscheint als 64×48-Picture-in-Picture oben rechts im oberen DeSmuME-Bildschirm. SoulBuddy-Meldungen werden weiterhin darüber gezeichnet.", "The received stream appears as a 64×48 picture-in-picture in the top-right of DeSmuME's top screen. SoulBuddy notifications are still drawn above it.", "Le stream reçu apparaît en image dans l’image 64×48 en haut à droite de l’écran supérieur de DeSmuME. Les notifications SoulBuddy restent affichées par-dessus.", "El stream recibido aparece como imagen superpuesta de 64×48 en la esquina superior derecha de la pantalla superior de DeSmuME. Las notificaciones de SoulBuddy siguen mostrándose encima.", "Lo stream ricevuto appare come picture-in-picture 64×48 in alto a destra nello schermo superiore di DeSmuME. Le notifiche SoulBuddy restano visualizzate sopra.", "受信ストリームはDeSmuME上画面の右上に64×48のピクチャーインピクチャーで表示されます。SoulBuddy通知はその上に表示されます。"),
        P("Zuletzt verwendet", "Last used", "Dernier profil utilisé", "Último utilizado", "Ultimo utilizzato", "前回使用"),
        P("Mit diesem Profil starten", "Start with this profile", "Démarrer avec ce profil", "Iniciar con este perfil", "Avvia con questo profilo", "このプロフィールで開始"),
        P("Wähle bei jedem Start den gewünschten SoulLocke-Run oder gib eine neue SoulLocke-Session ein.", "Choose the desired SoulLocke run on every start or enter a new SoulLocke session.", "À chaque démarrage, choisis le run SoulLocke souhaité ou saisis une nouvelle session SoulLocke.", "En cada inicio, elige el run de SoulLocke deseado o introduce una nueva sesión de SoulLocke.", "A ogni avvio scegli il run SoulLocke desiderato oppure inserisci una nuova sessione SoulLocke.", "起動するたびに使用するSoulLockeランを選ぶか、新しいSoulLockeセッションを入力してください。"),
        P("Spielername", "Player name", "Nom du joueur", "Nombre del jugador", "Nome giocatore", "プレイヤー名"),
        P("Dein Spielername", "Your player name", "Ton nom de joueur", "Tu nombre de jugador", "Il tuo nome giocatore", "プレイヤー名"),
        P("SoulLocke-Link", "SoulLocke link", "Lien SoulLocke", "Enlace de SoulLocke", "Link SoulLocke", "SoulLockeリンク"),
        P("SoulLocke-Passwort", "SoulLocke password", "Mot de passe SoulLocke", "Contraseña de SoulLocke", "Password SoulLocke", "SoulLockeパスワード"),
        P("Hauptfenster anzeigen", "Show main window", "Afficher la fenêtre principale", "Mostrar ventana principal", "Mostra finestra principale", "メインウィンドウを表示"),
        P("SoulBuddy liest Partnerdaten ausschließlich aus SoulLocke und schreibt ausschließlich deinen eigenen Run zurück.", "SoulBuddy reads partner data only from SoulLocke and only writes back to your own run.", "SoulBuddy lit les données du partenaire uniquement depuis SoulLocke et écrit uniquement dans ton propre run.", "SoulBuddy lee los datos del compañero únicamente desde SoulLocke y solo escribe en tu propio run.", "SoulBuddy legge i dati del compagno esclusivamente da SoulLocke e scrive solo nel tuo run.", "SoulBuddyはパートナーデータをSoulLockeからのみ読み取り、自分のランにのみ書き込みます。"),
        P("Ausgeschaltet läuft SoulBuddy nach deiner Auswahl nur im Hintergrund. Sync, Collector und Overlay bleiben aktiv.", "When disabled, SoulBuddy runs only in the background after your selection. Sync, collector and overlay remain active.", "Si cette option est désactivée, SoulBuddy fonctionne uniquement en arrière-plan après ta sélection. La synchro, le collecteur et l’overlay restent actifs.", "Si se desactiva, SoulBuddy funciona solo en segundo plano después de tu selección. La sincronización, el collector y el overlay siguen activos.", "Se disattivato, SoulBuddy viene eseguito solo in background dopo la selezione. Sincronizzazione, collector e overlay restano attivi.", "オフにすると、選択後はSoulBuddyがバックグラウンドのみで動作します。同期・コレクター・オーバーレイは引き続き有効です。"),
        P("Starten", "Start", "Démarrer", "Iniciar", "Avvia", "開始"),
        P("Kopieren", "Copy", "Copier", "Copiar", "Copia", "コピー"),
        P("Noch nicht gestartet", "Not started yet", "Pas encore démarré", "Aún no iniciado", "Non ancora avviato", "未開始"),
        P("Spiel verbunden", "Game connected", "Jeu connecté", "Juego conectado", "Gioco connesso", "ゲーム接続済み"),
        P("Server verbunden", "Server connected", "Serveur connecté", "Servidor conectado", "Server connesso", "サーバー接続済み"),
        P("Emulator wird gesucht …", "Looking for emulator …", "Recherche de l’émulateur …", "Buscando emulador …", "Ricerca emulatore …", "エミュレーターを検索中…"),
        P("Collector nicht verbunden", "Collector not connected", "Collecteur non connecté", "Collector no conectado", "Collector non connesso", "コレクター未接続"),
        P("Synchronisierung über Server nicht erfolgreich", "Server synchronization unsuccessful", "Échec de la synchronisation serveur", "La sincronización con el servidor no se ha realizado correctamente", "Sincronizzazione con il server non riuscita", "サーバー同期に失敗"),
        P("Partnerdaten werden geladen …", "Loading partner data …", "Chargement des données du partenaire …", "Cargando datos del compañero …", "Caricamento dati del compagno …", "パートナーデータを読み込み中…"),
        P("Aktiv: wird ermittelt …", "Active: detecting …", "Actif : détection …", "Activo: detectando …", "Attivo: rilevamento …", "使用中: 検出中…"),
        P("Kein Pokémon ausgewählt", "No Pokémon selected", "Aucun Pokémon sélectionné", "Ningún Pokémon seleccionado", "Nessun Pokémon selezionato", "ポケモン未選択"),
        P("Wähle ein Pokémon aus, um seine Details anzuzeigen.", "Select a Pokémon to show its details.", "Sélectionne un Pokémon pour afficher ses détails.", "Selecciona un Pokémon para ver sus detalles.", "Seleziona un Pokémon per visualizzarne i dettagli.", "詳細を表示するポケモンを選択してください。"),
        P("LIVE-STATUS", "LIVE STATUS", "STATUT EN DIRECT", "ESTADO EN VIVO", "STATO LIVE", "ライブステータス"),
        P("Warte auf Live-Daten aus dem Emulator …", "Waiting for live data from the emulator …", "En attente des données en direct de l’émulateur …", "Esperando datos en vivo del emulador …", "In attesa dei dati live dall’emulatore …", "エミュレーターのライブデータを待機中…"),
        P("Erkundet gerade die Welt", "Exploring the world", "Explore le monde", "Explorando el mundo", "Esplorazione del mondo", "フィールドを探索中"),
        P("Aufenthaltsort wird ermittelt", "Detecting location", "Détection du lieu", "Detectando ubicación", "Rilevamento posizione", "場所を検出中"),
        P("Gegner: wird ermittelt …", "Opponent: detecting …", "Adversaire : détection …", "Rival: detectando …", "Avversario: rilevamento …", "相手: 検出中…"),
        P("Wilder Kampf", "Wild battle", "Combat sauvage", "Combate salvaje", "Lotta con Pokémon selvatico", "野生ポケモン戦"),
        P("Trainerkampf", "Trainer battle", "Combat de Dresseur", "Combate de Entrenador", "Lotta con Allenatore", "トレーナー戦"),
        P("Kampf erkannt", "Battle detected", "Combat détecté", "Combate detectado", "Lotta rilevata", "バトルを検出"),
        P("LIVE ENCOUNTER", "LIVE ENCOUNTER", "RENCONTRE EN DIRECT", "ENCUENTRO EN VIVO", "INCONTRO LIVE", "ライブエンカウント"),
        P("SoulLocke synchronisiert", "SoulLocke synchronized", "SoulLocke synchronisé", "SoulLocke sincronizado", "SoulLocke sincronizzato", "SoulLocke同期済み"),
        P("Geschlechtslos", "Genderless", "Asexué", "Sin género", "Senza sesso", "性別不明"),
        P("Weiblich", "Female", "Femelle", "Hembra", "Femmina", "メス"),
        P("Männlich", "Male", "Mâle", "Macho", "Maschio", "オス"),
        P("Ja", "Yes", "Oui", "Sí", "Sì", "はい"),
        P("Nein", "No", "Non", "No", "No", "いいえ"),
        P("Unbekannt", "Unknown", "Inconnu", "Desconocido", "Sconosciuto", "不明"),
        P("Unbekannter Ort", "Unknown location", "Lieu inconnu", "Ubicación desconocida", "Luogo sconosciuto", "不明な場所"),
        P("Kein Stream verbunden", "No stream connected", "Aucun stream connecté", "Ningún stream conectado", "Nessuno stream connesso", "ストリーム未接続"),
        P("Stream nicht gestartet", "Stream not started", "Stream non démarré", "Stream no iniciado", "Stream non avviato", "ストリーム未開始"),
        P("Verbindung zum Stream wird hergestellt …", "Connecting to stream …", "Connexion au stream …", "Conectando al stream …", "Connessione allo stream …", "ストリームに接続中…"),
        P("Aufnahme gestartet · warte auf DeSmuME-Frames …", "Capture started · waiting for DeSmuME frames …", "Capture démarrée · attente des images DeSmuME …", "Captura iniciada · esperando fotogramas de DeSmuME …", "Cattura avviata · attesa frame DeSmuME …", "キャプチャ開始 · DeSmuMEフレームを待機中…"),
        P("Aufnahme und Stream laufen", "Capture and stream running", "Capture et stream actifs", "Captura y stream activos", "Cattura e stream attivi", "キャプチャとストリーム実行中"),
        P("Stream verbunden · warte auf Videoframes …", "Stream connected · waiting for video frames …", "Stream connecté · attente des images vidéo …", "Stream conectado · esperando fotogramas …", "Stream connesso · attesa frame video …", "ストリーム接続済み · 映像フレーム待機中…"),
        P("Stream wird angezeigt", "Stream is displayed", "Stream affiché", "Stream visible", "Stream visualizzato", "ストリーム表示中")
    ];

    private static readonly string[][] FragmentPhrases =
    [
        P("Aktives Pokémon: ", "Active Pokémon: ", "Pokémon actif : ", "Pokémon activo: ", "Pokémon attivo: ", "使用中のポケモン: "),
        P("Aktiv: ", "Active: ", "Actif : ", "Activo: ", "Attivo: ", "使用中: "),
        P("Gegner: ", "Opponent: ", "Adversaire : ", "Rival: ", "Avversario: ", "相手: "),
        P("Spezies: ", "Species: ", "Espèce : ", "Especie: ", "Specie: ", "種族: "),
        P("Geschlecht: ", "Gender: ", "Sexe : ", "Sexo: ", "Sesso: ", "性別: "),
        P("Wesen: ", "Nature: ", "Nature : ", "Naturaleza: ", "Natura: ", "性格: "),
        P("Fähigkeit: ", "Ability: ", "Talent : ", "Habilidad: ", "Abilità: ", "特性: "),
        P("Pokéball: ", "Poké Ball: ", "Poké Ball : ", "Poké Ball: ", "Poké Ball: ", "ボール: "),
        P("Fanglevel: ", "Caught at level: ", "Niveau de capture : ", "Nivel de captura: ", "Livello di cattura: ", "捕獲レベル: "),
        P("Fangort: ", "Caught at: ", "Lieu de capture : ", "Lugar de captura: ", "Luogo di cattura: ", "捕獲場所: "),
        P("Fangort-ID: ", "Location ID: ", "ID du lieu : ", "ID de ubicación: ", "ID luogo: ", "場所ID: "),
        P("Trainer-ID: ", "Trainer ID: ", "ID Dresseur : ", "ID Entrenador: ", "ID Allenatore: ", "トレーナーID: "),
        P("Secret-ID: ", "Secret ID: ", "ID secret : ", "ID secreto: ", "ID segreto: ", "シークレットID: "),
        P("Erstmals erkannt: ", "First detected: ", "Détecté pour la première fois : ", "Detectado por primera vez: ", "Rilevato per la prima volta: ", "初回検出: "),
        P("Zuletzt gesehen: ", "Last seen: ", "Vu pour la dernière fois : ", "Visto por última vez: ", "Ultimo avvistamento: ", "最終確認: "),
        P("Technische Daten", "Technical data", "Données techniques", "Datos técnicos", "Dati tecnici", "技術データ"),
        P("Spiel: unbekannt", "Game: unknown", "Jeu : inconnu", "Juego: desconocido", "Gioco: sconosciuto", "ゲーム: 不明"),
        P("Unbekannter Fangort (", "Unknown catch location (", "Lieu de capture inconnu (", "Lugar de captura desconocido (", "Luogo di cattura sconosciuto (", "不明な捕獲場所 ("),
        P("Unbekannter Ort (", "Unknown location (", "Lieu inconnu (", "Ubicación desconocida (", "Luogo sconosciuto (", "不明な場所 ("),
        P("Gegner erkannt: ", "Opponent detected: ", "Adversaire détecté : ", "Rival detectado: ", "Avversario rilevato: ", "相手を検出: "),
        P("Aufenthalt: ", "Location: ", "Lieu : ", "Ubicación: ", "Posizione: ", "場所: "),
        P("Kampf begonnen", "Battle started", "Combat commencé", "Combate iniciado", "Lotta iniziata", "バトル開始"),
        P("Kampf beendet", "Battle ended", "Combat terminé", "Combate terminado", "Lotta terminata", "バトル終了"),
        P("SoulBuddy wird gestartet …", "SoulBuddy is starting …", "Démarrage de SoulBuddy …", "SoulBuddy se está iniciando …", "Avvio di SoulBuddy …", "SoulBuddyを起動中…"),
        P("Startfehler: ", "Startup error: ", "Erreur de démarrage : ", "Error de inicio: ", "Errore di avvio: ", "起動エラー: "),
        P("Aktualisierungsfehler: ", "Refresh error: ", "Erreur d’actualisation : ", "Error de actualización: ", "Errore di aggiornamento: ", "更新エラー: "),
        P("Stream konnte nicht gestartet werden: ", "Stream could not be started: ", "Impossible de démarrer le stream : ", "No se pudo iniciar el stream: ", "Impossibile avviare lo stream: ", "ストリームを開始できませんでした: "),
        P("Stream reagiert nicht · Verbindung wird wiederhergestellt …", "Stream not responding · reconnecting …", "Le stream ne répond pas · reconnexion …", "El stream no responde · reconectando …", "Lo stream non risponde · riconnessione …", "ストリームが応答しません · 再接続中…"),
        P("Stream getrennt · Verbindung wird wiederhergestellt …", "Stream disconnected · reconnecting …", "Stream déconnecté · reconnexion …", "Stream desconectado · reconectando …", "Stream disconnesso · riconnessione …", "ストリーム切断 · 再接続中…")
    ];

    private static readonly Dictionary<string, string[]> OverlayTemplates = new(StringComparer.Ordinal)
    {
        ["catchable"] = P("{0} fangbar! ({1})", "{0} can be caught! ({1})", "{0} peut être capturé ! ({1})", "¡Se puede capturar a {0}! ({1})", "{0} può essere catturato! ({1})", "{0}を捕まえられる！ ({1})"),
        ["shinyCatchable"] = P("Shiny {0} fangbar! ({1})", "Shiny {0} can be caught! ({1})", "Shiny {0} peut être capturé ! ({1})", "¡Se puede capturar al Shiny {0}! ({1})", "Shiny {0} può essere catturato! ({1})", "色違い{0}を捕まえられる！ ({1})"),
        ["caught"] = P("{0} gefangen! ({1})", "{0} caught! ({1})", "{0} capturé ! ({1})", "¡{0} capturado! ({1})", "{0} catturato! ({1})", "{0}を捕まえた！ ({1})"),
        ["catchFailed"] = P("Fang fehlgeschlagen! ({0})", "Catch failed! ({0})", "Capture échouée ! ({0})", "¡Captura fallida! ({0})", "Cattura fallita! ({0})", "捕獲失敗！ ({0})"),
        ["partnerCaught"] = P("Partner hat {0} gefangen! ({1})", "Partner caught {0}! ({1})", "Le partenaire a capturé {0} ! ({1})", "¡El compañero capturó a {0}! ({1})", "Il compagno ha catturato {0}! ({1})", "パートナーが{0}を捕まえた！ ({1})"),
        ["partnerCatchFailed"] = P("Partner konnte {0} nicht fangen! ({1})", "Partner failed to catch {0}! ({1})", "Le partenaire n’a pas réussi à capturer {0} ! ({1})", "¡El compañero no pudo capturar a {0}! ({1})", "Il compagno non è riuscito a catturare {0}! ({1})", "パートナーは{0}を捕まえられなかった！ ({1})"),
        ["partnerKo"] = P("Partner K.O. - {0} K.O., {1} raus!", "Partner K.O. - {0} fainted, {1} is out!", "K.O. partenaire - {0} est K.O., {1} doit sortir !", "K.O. del compañero - {0} cayó, ¡{1} queda fuera!", "K.O. del compagno - {0} è K.O., {1} è fuori!", "パートナーK.O. - {0}が倒れたため、{1}も離脱！"),
        ["partnerBoxed"] = P("Partner hat {0} in die Box gelegt! (SoulLink: {1})", "Partner boxed {0}! (SoulLink: {1})", "Le partenaire a placé {0} dans la Boîte ! (SoulLink : {1})", "¡El compañero guardó a {0} en la Caja! (SoulLink: {1})", "Il compagno ha messo {0} nel Box! (SoulLink: {1})", "パートナーが{0}をボックスに預けた！ (SoulLink: {1})"),
        ["generic"] = P("{0}: Nuzlocke-Event", "{0}: Nuzlocke event", "{0} : événement Nuzlocke", "{0}: evento Nuzlocke", "{0}: evento Nuzlocke", "{0}: Nuzlockeイベント"),
        ["linkedPokemon"] = P("verknüpftes Pokémon", "linked Pokémon", "Pokémon lié", "Pokémon vinculado", "Pokémon collegato", "リンクされたポケモン"),
        ["unknownLocation"] = P("Unbekannter Ort", "Unknown location", "Lieu inconnu", "Ubicación desconocida", "Luogo sconosciuto", "不明な場所")
    };

    public static event EventHandler? LanguageChanged;

    public static AppLanguage CurrentLanguage
    {
        get
        {
            lock (Sync)
                return _currentLanguage;
        }
    }

    public static string CurrentFlag => CurrentLanguage switch
    {
        AppLanguage.English => "🇬🇧",
        AppLanguage.French => "🇫🇷",
        AppLanguage.Spanish => "🇪🇸",
        AppLanguage.Italian => "🇮🇹",
        AppLanguage.Japanese => "🇯🇵",
        _ => "🇩🇪"
    };

    static LocalizationService()
    {
        TryLoadPokemonNames(GetPokemonCachePath());
        StartPokemonNameDownloadIfNeeded();
    }

    public static void SetLanguage(AppLanguage language)
    {
        bool changed;
        lock (Sync)
        {
            changed = _currentLanguage != language;
            _currentLanguage = language;
        }

        SaveLanguage(language);
        StartPokemonNameDownloadIfNeeded();
        if (changed)
            LanguageChanged?.Invoke(null, EventArgs.Empty);
    }

    public static string Overlay(string key, params object?[] args)
    {
        if (!OverlayTemplates.TryGetValue(key, out var translations))
            return key;
        return string.Format(Get(translations), args);
    }

    public static string Ui(string source)
    {
        if (string.IsNullOrWhiteSpace(source))
            return source;

        var exact = FindPhrase(source, UiPhrases);
        var translated = exact is null ? source : Get(exact);

        foreach (var phrase in FragmentPhrases.OrderByDescending(item => item[0].Length))
        {
            if (translated.Contains(phrase[0], StringComparison.Ordinal))
                translated = translated.Replace(phrase[0], Get(phrase), StringComparison.Ordinal);
        }

        return TranslatePokemonNamesInText(translated);
    }

    public static bool IsTranslationOf(string? value, string canonicalGerman)
    {
        if (string.IsNullOrWhiteSpace(value))
            return false;
        var phrase = UiPhrases.Concat(FragmentPhrases)
            .FirstOrDefault(item => string.Equals(item[0], canonicalGerman, StringComparison.Ordinal));
        if (phrase is null)
            return string.Equals(value, canonicalGerman, StringComparison.Ordinal);
        return phrase.Any(item => string.Equals(item, value, StringComparison.Ordinal));
    }

    public static string PokemonName(int speciesId, string? fallback = null)
    {
        lock (Sync)
        {
            if (PokemonNames.TryGetValue(speciesId, out var names) &&
                names.TryGetValue(LanguageCode(CurrentLanguage), out var name) &&
                !string.IsNullOrWhiteSpace(name))
            {
                return name;
            }
        }

        return string.IsNullOrWhiteSpace(fallback)
            ? $"Pokémon #{speciesId}"
            : fallback!;
    }

    private static string TranslatePokemonNamesInText(string source)
    {
        Regex? regex;
        Dictionary<string, int> lookup;
        lock (Sync)
        {
            regex = _pokemonNameRegex;
            if (regex is null)
                return source;
            lookup = new Dictionary<string, int>(PokemonIdsByLocalizedName, StringComparer.OrdinalIgnoreCase);
        }

        return regex.Replace(source, match =>
        {
            if (!lookup.TryGetValue(match.Value, out var id))
                return match.Value;
            return PokemonName(id, match.Value);
        });
    }

    private static string[]? FindPhrase(string value, IEnumerable<string[]> phrases)
    {
        foreach (var phrase in phrases)
        {
            if (phrase.Any(item => string.Equals(item, value, StringComparison.Ordinal)))
                return phrase;
        }
        return null;
    }

    private static string Get(string[] translations) => translations[LanguageIndex(CurrentLanguage)];

    private static int LanguageIndex(AppLanguage language) => language switch
    {
        AppLanguage.English => 1,
        AppLanguage.French => 2,
        AppLanguage.Spanish => 3,
        AppLanguage.Italian => 4,
        AppLanguage.Japanese => 5,
        _ => 0
    };

    private static string LanguageCode(AppLanguage language) => language switch
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

    private static AppLanguage LoadSavedLanguage()
    {
        try
        {
            var path = GetLanguagePath();
            if (!File.Exists(path))
                return AppLanguage.German;
            return File.ReadAllText(path).Trim().ToLowerInvariant() switch
            {
                "en" => AppLanguage.English,
                "fr" => AppLanguage.French,
                "es" => AppLanguage.Spanish,
                "it" => AppLanguage.Italian,
                "ja" => AppLanguage.Japanese,
                _ => AppLanguage.German
            };
        }
        catch
        {
            return AppLanguage.German;
        }
    }

    private static void SaveLanguage(AppLanguage language)
    {
        try
        {
            var path = GetLanguagePath();
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, LanguageCode(language), new UTF8Encoding(false));
        }
        catch
        {
            // Language switching must remain usable even if the preference cannot be persisted.
        }
    }

    private static string GetLanguagePath() =>
        Path.Combine(AppContext.BaseDirectory, "data", "language.txt");

    private static string GetPokemonCachePath() =>
        Path.Combine(AppContext.BaseDirectory, "data", "pokemon-species-names.csv");

    private static void StartPokemonNameDownloadIfNeeded()
    {
        lock (Sync)
        {
            if (PokemonNames.Count >= 493 || _pokemonDownloadStarted)
                return;
            _pokemonDownloadStarted = true;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                var csv = await HttpClient.GetStringAsync(PokemonNamesUrl);
                var path = GetPokemonCachePath();
                Directory.CreateDirectory(Path.GetDirectoryName(path)!);
                await File.WriteAllTextAsync(path, csv, new UTF8Encoding(false));
                TryLoadPokemonNames(path);
                LanguageChanged?.Invoke(null, EventArgs.Empty);
            }
            catch
            {
                // Existing collector names remain the fallback when the optional name list is unavailable.
            }
        });
    }

    private static void TryLoadPokemonNames(string path)
    {
        try
        {
            if (!File.Exists(path))
                return;

            var byId = new Dictionary<int, Dictionary<string, string>>();
            foreach (var line in File.ReadLines(path).Skip(1))
            {
                var fields = line.Split(',', 4);
                if (fields.Length < 3 ||
                    !int.TryParse(fields[0], out var speciesId) ||
                    speciesId is < 1 or > 493 ||
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

                var name = fields[2].Trim().Trim('"');
                if (string.IsNullOrWhiteSpace(name))
                    continue;

                if (!byId.TryGetValue(speciesId, out var names))
                {
                    names = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                    byId[speciesId] = names;
                }
                names[language] = name;
            }

            if (byId.Count < 493)
                return;

            lock (Sync)
            {
                PokemonNames.Clear();
                PokemonIdsByLocalizedName.Clear();
                foreach (var pair in byId)
                {
                    PokemonNames[pair.Key] = pair.Value;
                    foreach (var name in pair.Value.Values)
                    {
                        if (!string.IsNullOrWhiteSpace(name))
                            PokemonIdsByLocalizedName[name] = pair.Key;
                    }
                }

                var alternatives = PokemonIdsByLocalizedName.Keys
                    .OrderByDescending(name => name.Length)
                    .Select(Regex.Escape);
                _pokemonNameRegex = new Regex(
                    $"(?<![\\p{{L}}\\p{{N}}])(?:{string.Join("|", alternatives)})(?![\\p{{L}}\\p{{N}}])",
                    RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
            }
        }
        catch
        {
            // Keep the current/fallback names if the cache is temporarily unavailable.
        }
    }
}
