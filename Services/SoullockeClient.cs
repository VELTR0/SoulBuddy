using System.Net.Http.Json;
using System.Text.Json;
using SoulBuddy.Models;

namespace SoulBuddy.Services;

public sealed class SoullockeClient
{
    private const string ApiBaseUrl = "https://soullocke.com:7001/api/";
    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private Dictionary<string, PlayerMappingEntry>? _playerMapping;
    private string _sessionGameName = string.Empty;
    private bool _initialized;

    public SoullockeClient(HttpClient httpClient, AppConfig config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    public async Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken)
    {
        var all = await LoadAllRunsAsync(cancellationToken);
        return all.TryGetValue(_config.PlayerId, out var run)
            ? run
            : throw new InvalidOperationException($"Der Soullocke-Run für {_config.PlayerName} ({_config.PlayerId}) wurde nicht gefunden.");
    }

    public async Task<IReadOnlyDictionary<string, SoullockeRun>> LoadAllRunsAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var mapping = _playerMapping ?? throw new InvalidOperationException("Die Soullocke-Spielerzuordnung wurde nicht initialisiert.");
        var request = new LoadRunsRequest
        {
            SessionId = _config.SessionId,
            Players = mapping.Keys.Select(id => new LoadRunPlayer { PlayerId = id, RunNumber = _config.RunNumber }).ToList(),
            PlayerMapping = mapping
        };

        Console.WriteLine(
            $"[SOULLOCKE-HTTP] LOAD START: Session='{_config.SessionId}', lokaler Spieler='{_config.PlayerName}'/" +
            $"{_config.PlayerId}, Run={_config.RunNumber}, Spieler=[{string.Join(", ", mapping.Keys)}].");

        using var response = await _httpClient.PostAsJsonAsync(ApiBaseUrl + "game/batchLoadRuns", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Console.WriteLine(
            $"[SOULLOCKE-HTTP] LOAD RESPONSE: HTTP {(int)response.StatusCode} {response.ReasonPhrase}, " +
            $"BodyLength={body.Length}.");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Soullocke konnte nicht geladen werden: {(int)response.StatusCode} {body}");

        var result = JsonSerializer.Deserialize<BatchLoadResponse>(body, JsonOptions)?.PlayerData
            ?? throw new InvalidOperationException("Soullocke hat ungültige Run-Daten zurückgegeben.");

        Console.WriteLine(
            $"[SOULLOCKE-HTTP] LOAD OK: " +
            string.Join(", ", result.Select(pair => $"{pair.Key}={pair.Value.Encounters.Count} Begegnungen")));

        LogAllPlayerEncounters(result, mapping);
        return result;
    }

    public async Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken)
    {
        await SaveRunForPlayerAsync(_config.PlayerId, encounters, cancellationToken);
        await AddPartnerLocationAliasesAsync(encounters, cancellationToken);
        await SynchronizeLinkedPartnerEncountersAsync(encounters, cancellationToken);
    }

    public async Task<bool> MarkLinkedPartnerBroFailedAsync(string location, CancellationToken cancellationToken)
    {
        var runs = await LoadAllRunsAsync(cancellationToken);
        foreach (var pair in runs)
        {
            if (string.Equals(pair.Key, _config.PlayerId, StringComparison.OrdinalIgnoreCase)) continue;

            var partnerKey = pair.Value.Encounters.Keys.FirstOrDefault(
                key => NormalizeLinkedLocation(key) == NormalizeLinkedLocation(location));
            if (partnerKey is null) continue;

            var partnerEncounter = pair.Value.Encounters[partnerKey];
            if (string.Equals(partnerEncounter.Status, "brofailed", StringComparison.OrdinalIgnoreCase)) return false;
            partnerEncounter.Status = "brofailed";
            await SaveRunForPlayerAsync(pair.Key, pair.Value.Encounters, cancellationToken);
            Console.WriteLine($"Soullocke: Partner-Begegnung „{partnerKey}“ einmalig auf Bro-Failed gesetzt.");
            return true;
        }
        Console.WriteLine($"[SOULLOCKE-HTTP] Kein Partner-Eintrag für Fangort '{location}' gefunden; Bro-Failed nicht gesetzt.");
        return false;
    }

    private async Task AddPartnerLocationAliasesAsync(
        Dictionary<string, SoullockeEncounter> localEncounters,
        CancellationToken cancellationToken)
    {
        var runs = await LoadAllRunsAsync(cancellationToken);
        var partnerRuns = runs
            .Where(pair => !string.Equals(pair.Key, _config.PlayerId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (partnerRuns.Length == 0)
        {
            Console.WriteLine("[SOULLOCKE-LINK] Kein weiterer Spieler gefunden; Orts-Key-Abgleich übersprungen.");
            return;
        }

        var changed = false;
        foreach (var localPair in localEncounters.ToArray())
        {
            var normalizedLocal = NormalizeLinkedLocation(localPair.Key);
            var partnerKey = partnerRuns
                .SelectMany(pair => pair.Value.Encounters.Keys)
                .FirstOrDefault(key => NormalizeLinkedLocation(key) == normalizedLocal);

            if (partnerKey is null || string.Equals(partnerKey, localPair.Key, StringComparison.Ordinal))
                continue;

            if (localEncounters.TryGetValue(partnerKey, out var existingAtPartnerKey))
            {
                if (existingAtPartnerKey.Pokemon > 0 && existingAtPartnerKey.Pokemon != localPair.Value.Pokemon)
                {
                    Console.WriteLine(
                        $"[SOULLOCKE-LINK] Partner-Alias '{partnerKey}' konnte für '{localPair.Key}' nicht angelegt werden, " +
                        "weil dort bereits ein anderes Pokémon gespeichert ist.");
                }
                continue;
            }

            localEncounters[partnerKey] = CloneEncounter(localPair.Value);
            changed = true;
            Console.WriteLine(
                $"[SOULLOCKE-LINK] Partner-kompatibler Orts-Alias angelegt: '{localPair.Key}' + '{partnerKey}'.");
        }

        if (!changed)
        {
            Console.WriteLine("[SOULLOCKE-LINK] Alle benötigten Partner-Orts-Aliase sind bereits vorhanden.");
            return;
        }

        await SaveRunForPlayerAsync(_config.PlayerId, localEncounters, cancellationToken);
        Console.WriteLine("[SOULLOCKE-LINK] Lokaler Run mit Partner-Orts-Alias gespeichert.");
    }

    private async Task SynchronizeLinkedPartnerEncountersAsync(
        IReadOnlyDictionary<string, SoullockeEncounter> localEncounters,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var mapping = _playerMapping ?? throw new InvalidOperationException("Die Soullocke-Spielerzuordnung wurde nicht initialisiert.");
        var runs = await LoadAllRunsAsync(cancellationToken);

        var localByLocation = localEncounters
            .Where(pair => !string.IsNullOrWhiteSpace(pair.Key) && pair.Value.Pokemon > 0)
            .GroupBy(pair => NormalizeLinkedLocation(pair.Key), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);

        foreach (var partner in mapping)
        {
            if (string.Equals(partner.Key, _config.PlayerId, StringComparison.OrdinalIgnoreCase))
                continue;

            if (!runs.TryGetValue(partner.Key, out var partnerRun))
            {
                Console.WriteLine(
                    $"[SOULLOCKE-LINK] Partner-Run für '{partner.Value.PlayerName}'/{partner.Key} wurde nicht gefunden; " +
                    "Spiegelung konnte nicht durchgeführt werden.");
                continue;
            }

            var partnerChanged = false;
            foreach (var localPair in localByLocation.Values)
            {
                var normalizedLocation = NormalizeLinkedLocation(localPair.Key);
                var partnerKey = partnerRun.Encounters.Keys.FirstOrDefault(
                    key => NormalizeLinkedLocation(key) == normalizedLocation) ?? localPair.Key;

                if (!partnerRun.Encounters.TryGetValue(partnerKey, out var partnerEncounter))
                {
                    partnerRun.Encounters[partnerKey] = CloneEncounter(localPair.Value);
                    partnerChanged = true;
                    Console.WriteLine(
                        $"[SOULLOCKE-LINK] Partner-Eintrag gespiegelt: Spieler='{partner.Value.PlayerName}'/{partner.Key}, " +
                        $"Ort='{partnerKey}', Pokémon=#{localPair.Value.Pokemon}, Spitzname='{localPair.Value.Nickname ?? "<leer>"}', " +
                        $"Status='{localPair.Value.Status}'.");
                    continue;
                }

                var pokemonChanged = partnerEncounter.Pokemon != localPair.Value.Pokemon;
                var nicknameChanged = !string.Equals(
                    partnerEncounter.Nickname,
                    localPair.Value.Nickname,
                    StringComparison.Ordinal);
                var statusChanged = !string.Equals(
                    partnerEncounter.Status,
                    localPair.Value.Status,
                    StringComparison.OrdinalIgnoreCase);

                if (!pokemonChanged && !nicknameChanged && !statusChanged)
                    continue;

                partnerEncounter.Pokemon = localPair.Value.Pokemon;
                partnerEncounter.Nickname = localPair.Value.Nickname;
                partnerEncounter.Status = localPair.Value.Status;
                partnerChanged = true;

                Console.WriteLine(
                    $"[SOULLOCKE-LINK] Partner-Eintrag aktualisiert: Spieler='{partner.Value.PlayerName}'/{partner.Key}, " +
                    $"Ort='{partnerKey}', Pokémon=#{localPair.Value.Pokemon}, Spitzname='{localPair.Value.Nickname ?? "<leer>"}', " +
                    $"Status='{localPair.Value.Status}' " +
                    $"(pokemonChanged={pokemonChanged}, nicknameChanged={nicknameChanged}, statusChanged={statusChanged}).");
            }

            if (!partnerChanged)
            {
                Console.WriteLine(
                    $"[SOULLOCKE-LINK] Partner '{partner.Value.PlayerName}'/{partner.Key} ist bereits vollständig " +
                    "mit dem lokalen Savegame synchron.");
                continue;
            }

            await SaveRunForPlayerAsync(partner.Key, partnerRun.Encounters, cancellationToken);
            Console.WriteLine(
                $"[SOULLOCKE-LINK] Gespiegelte Begegnungen für '{partner.Value.PlayerName}'/{partner.Key} gespeichert.");
        }
    }

    private static SoullockeEncounter CloneEncounter(SoullockeEncounter source) => new()
    {
        Pokemon = source.Pokemon,
        Nickname = source.Nickname,
        Status = source.Status
    };

    private async Task SaveRunForPlayerAsync(string playerId, Dictionary<string, SoullockeEncounter> encounters, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var mapping = _playerMapping ?? throw new InvalidOperationException("Die Soullocke-Spielerzuordnung wurde nicht initialisiert.");
        if (!mapping.TryGetValue(playerId, out var player))
            throw new InvalidOperationException($"Soullocke-Spieler {playerId} ist unbekannt.");

        var allRuns = await LoadAllRunsAsync(cancellationToken);
        if (!allRuns.TryGetValue(playerId, out var currentRun))
            throw new InvalidOperationException($"Der aktuelle Soullocke-Run für {player.PlayerName} ({playerId}) wurde vor dem Speichern nicht gefunden.");

        var query = $"game/saveRun?sessionId={Uri.EscapeDataString(_config.SessionId)}&" +
                    $"teamName={Uri.EscapeDataString(player.TeamName)}&" +
                    $"playerName={Uri.EscapeDataString(player.PlayerName)}&" +
                    $"authToken={Uri.EscapeDataString(_config.AuthToken)}";

        var request = new SaveRunRequest
        {
            PlayerId = playerId,
            RunNumber = currentRun.RunNumber > 0 ? currentRun.RunNumber : _config.RunNumber,
            GameName = _sessionGameName,
            Status = string.IsNullOrWhiteSpace(currentRun.Status) ? "open" : currentRun.Status,
            Encounters = encounters
        };

        Console.WriteLine(
            $"[SOULLOCKE-HTTP] SAVE START: Spieler='{player.PlayerName}'/{playerId}, Team='{player.TeamName}', " +
            $"Run={request.RunNumber}, Game='{request.GameName}', Status='{request.Status}', Begegnungen={encounters.Count}: " +
            string.Join(", ", encounters.Select(pair => $"'{pair.Key}'=#{pair.Value.Pokemon}/{pair.Value.Status}")));

        using var response = await _httpClient.PostAsJsonAsync(ApiBaseUrl + query, request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Console.WriteLine(
            $"[SOULLOCKE-HTTP] SAVE RESPONSE: HTTP {(int)response.StatusCode} {response.ReasonPhrase}, " +
            $"BodyLength={body.Length}, Body='{Truncate(body, 800)}'.");

        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Soullocke konnte nicht gespeichert werden: {(int)response.StatusCode} {body}");

        Console.WriteLine($"[SOULLOCKE-HTTP] SAVE OK für '{player.PlayerName}' mit {encounters.Count} Begegnungen.");
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized) return;
        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized) return;
            if (string.IsNullOrWhiteSpace(_config.SessionId)) throw new InvalidOperationException("Der Soullocke-Link enthält keine gültige Session-ID.");
            var session = await LoadSessionMetadataAsync(cancellationToken);
            _sessionGameName = NormalizeSessionGameName(session.Settings.Game);
            ResolvePlayerAssignment(session);
            _config.AuthToken = await AuthenticateAsync(cancellationToken);
            _initialized = true;
            Console.WriteLine(
                $"Soullocke zugeordnet: {_config.PlayerName} → {_config.PlayerId} / {_config.TeamName}; " +
                $"Session-Spiel='{_sessionGameName}'.");
        }
        finally { _initializationLock.Release(); }
    }

    private async Task<SoullockeSessionResponse> LoadSessionMetadataAsync(CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(ApiBaseUrl + $"session/{Uri.EscapeDataString(_config.SessionId)}", cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Die Soullocke-Sitzung konnte nicht geladen werden: {(int)response.StatusCode} {body}");
        return JsonSerializer.Deserialize<SoullockeSessionResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Soullocke hat ungültige Sitzungsdaten zurückgegeben.");
    }

    private void ResolvePlayerAssignment(SoullockeSessionResponse session)
    {
        var entries = new List<(string PlayerId,string TeamName,string PlayerName)>();
        var index = 1;
        foreach (var team in session.Settings.Teams)
            foreach (var player in team.Players)
                entries.Add(($"player{index++}", team.Name, player));
        var matches = entries.Where(e => string.Equals(e.PlayerName.Trim(), _config.PlayerName.Trim(), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1)
        {
            var names = string.Join(", ", entries.Select(e => e.PlayerName));
            throw new InvalidOperationException(matches.Length == 0
                ? $"Der Spielername „{_config.PlayerName}“ wurde in Soullocke nicht gefunden. Verfügbare Namen: {names}"
                : $"Der Spielername „{_config.PlayerName}“ kommt mehrfach in Soullocke vor und ist nicht eindeutig.");
        }
        var local = matches[0];
        _config.PlayerId = local.PlayerId;
        _config.TeamName = local.TeamName;
        _config.PlayerName = local.PlayerName;
        _playerMapping = entries.ToDictionary(e => e.PlayerId, e => new PlayerMappingEntry { TeamName=e.TeamName, PlayerName=e.PlayerName }, StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> AuthenticateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.AuthToken)) throw new InvalidOperationException("Bitte gib das Soullocke-Passwort ein.");
        using var response = await _httpClient.PostAsJsonAsync(ApiBaseUrl + "session/validate-password",
            new { sessionId = _config.SessionId, password = _config.AuthToken }, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Das Soullocke-Passwort konnte nicht geprüft werden: {(int)response.StatusCode} {body}");
        var result = JsonSerializer.Deserialize<SoullockePasswordValidationResponse>(body, JsonOptions);
        if (result is null || !result.IsValid || string.IsNullOrWhiteSpace(result.AuthToken)) throw new InvalidOperationException("Das eingegebene Soullocke-Passwort ist ungültig.");
        return result.AuthToken;
    }

    private static void LogAllPlayerEncounters(
        IReadOnlyDictionary<string, SoullockeRun> runs,
        IReadOnlyDictionary<string, PlayerMappingEntry> mapping)
    {
        Console.WriteLine("[SOULLOCKE-HTTP] VOLLSTÄNDIGE BEGEGNUNGSLISTEN ALLER SPIELER:");

        foreach (var playerRun in runs.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            mapping.TryGetValue(playerRun.Key, out var player);
            var playerName = player?.PlayerName ?? "<unbekannt>";
            var teamName = player?.TeamName ?? "<unbekannt>";

            Console.WriteLine(
                $"[SOULLOCKE-HTTP] PLAYER {playerRun.Key}: Name='{playerName}', Team='{teamName}', " +
                $"Run={playerRun.Value.RunNumber}, Game='{playerRun.Value.GameName}', Status='{playerRun.Value.Status}', " +
                $"Begegnungen={playerRun.Value.Encounters.Count}.");

            if (playerRun.Value.Encounters.Count == 0)
            {
                Console.WriteLine($"[SOULLOCKE-HTTP]   {playerRun.Key}: <keine Begegnungen>");
                continue;
            }

            foreach (var encounter in playerRun.Value.Encounters.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    $"[SOULLOCKE-HTTP]   {playerRun.Key}: Ort-Key='{encounter.Key}', " +
                    $"Pokémon=#{encounter.Value.Pokemon}, " +
                    $"Spitzname='{encounter.Value.Nickname ?? "<leer>"}', " +
                    $"Status='{encounter.Value.Status}'.");
            }
        }
    }

    private static string NormalizeLinkedLocation(string location)
    {
        var normalized = new string(location
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        return normalized switch
        {
            "darkcave" or "dunkelhöhle" or "dunkelhohle" => "darkcave",
            "sprouttower" or "knofensaturm" => "sprouttower",
            _ => normalized
        };
    }

    private static string NormalizeSessionGameName(string? gameName)
    {
        var normalized = (gameName ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Die Soullocke-Sitzung enthält kein gültiges Spiel.");

        return normalized switch
        {
            "heartgold" or "heart-gold" or "hg" => "heartgold",
            "soulsilver" or "soul-silver" or "ss" => "soulsilver",
            _ => normalized
        };
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength ? value : value[..maxLength] + "…";
}
