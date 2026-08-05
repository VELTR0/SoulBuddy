using System.Net.Http.Json;
using System.Text.Json;
using SoulBuddy.Models;

namespace SoulBuddy.Services;

public sealed class SoullockeClient
{
    private const string ApiBaseUrl = "https://soullocke.com:7001/api/";
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

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
        var allRuns = await LoadAllRunsAsync(cancellationToken);
        return allRuns.TryGetValue(_config.PlayerId, out var run)
            ? run
            : throw new InvalidOperationException(
                $"Der Soullocke-Run für {_config.PlayerName} ({_config.PlayerId}) wurde nicht gefunden.");
    }

    public async Task<IReadOnlyDictionary<string, SoullockeRun>> LoadAllRunsAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var mapping = _playerMapping
            ?? throw new InvalidOperationException("Die Soullocke-Spielerzuordnung wurde nicht initialisiert.");

        var request = new LoadRunsRequest
        {
            SessionId = _config.SessionId,
            Players = mapping.Keys
                .Select(id => new LoadRunPlayer
                {
                    PlayerId = id,
                    RunNumber = _config.RunNumber
                })
                .ToList(),
            PlayerMapping = mapping
        };

        Console.WriteLine(
            $"[SOULLOCKE-HTTP] LOAD START: Session='{_config.SessionId}', " +
            $"lokaler Spieler='{_config.PlayerName}'/{_config.PlayerId}, Run={_config.RunNumber}, " +
            $"Spieler=[{string.Join(", ", mapping.Keys)}].");

        using var response = await _httpClient.PostAsJsonAsync(
            ApiBaseUrl + "game/batchLoadRuns",
            request,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Console.WriteLine(
            $"[SOULLOCKE-HTTP] LOAD RESPONSE: HTTP {(int)response.StatusCode} {response.ReasonPhrase}, " +
            $"BodyLength={body.Length}.");

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Soullocke konnte nicht geladen werden: {(int)response.StatusCode} {body}");
        }

        var result = JsonSerializer.Deserialize<BatchLoadResponse>(body, JsonOptions)?.PlayerData
            ?? throw new InvalidOperationException("Soullocke hat ungültige Run-Daten zurückgegeben.");

        Console.WriteLine(
            "[SOULLOCKE-HTTP] LOAD OK: " +
            string.Join(", ", result.Select(pair =>
                $"{pair.Key}={pair.Value.Encounters.Count} Begegnungen")));

        LogAllPlayerEncounters(result, mapping);
        return result;
    }

    public async Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken)
    {
        await SaveLocalRunAsync(encounters, cancellationToken);
    }

    public Task<bool> MarkLinkedPartnerBroFailedAsync(
        string location,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine(
            $"[SOULLOCKE-SYNC] Partnerstatus für Ort '{location}' wird nicht geschrieben. " +
            "SoulBuddy aktualisiert ausschließlich den eigenen, über den Spielernamen zugeordneten Run.");
        return Task.FromResult(false);
    }

    private async Task SaveLocalRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var mapping = _playerMapping
            ?? throw new InvalidOperationException("Die Soullocke-Spielerzuordnung wurde nicht initialisiert.");

        if (!mapping.TryGetValue(_config.PlayerId, out var localPlayer))
        {
            throw new InvalidOperationException(
                $"Der anhand des Spielernamens zugeordnete Soullocke-Spieler {_config.PlayerId} ist unbekannt.");
        }

        var allRuns = await LoadAllRunsAsync(cancellationToken);
        if (!allRuns.TryGetValue(_config.PlayerId, out var currentRun))
        {
            throw new InvalidOperationException(
                $"Der aktuelle Soullocke-Run für {_config.PlayerName} ({_config.PlayerId}) " +
                "wurde vor dem Speichern nicht gefunden.");
        }

        var query =
            $"game/saveRun?sessionId={Uri.EscapeDataString(_config.SessionId)}&" +
            $"teamName={Uri.EscapeDataString(localPlayer.TeamName)}&" +
            $"playerName={Uri.EscapeDataString(localPlayer.PlayerName)}&" +
            $"authToken={Uri.EscapeDataString(_config.AuthToken)}";

        var request = new SaveRunRequest
        {
            PlayerId = _config.PlayerId,
            RunNumber = currentRun.RunNumber > 0
                ? currentRun.RunNumber
                : _config.RunNumber,
            GameName = _sessionGameName,
            Status = string.IsNullOrWhiteSpace(currentRun.Status)
                ? "open"
                : currentRun.Status,
            Encounters = encounters
        };

        Console.WriteLine(
            $"[SOULLOCKE-HTTP] SAVE OWN RUN START: " +
            $"Spieler='{localPlayer.PlayerName}'/{_config.PlayerId}, " +
            $"Run={request.RunNumber}, Game='{request.GameName}', Status='{request.Status}', " +
            $"Begegnungen={encounters.Count}: " +
            string.Join(", ", encounters.Select(pair =>
                $"'{pair.Key}'=#{pair.Value.Pokemon}/{pair.Value.Status}")));

        using var response = await _httpClient.PostAsJsonAsync(
            ApiBaseUrl + query,
            request,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        Console.WriteLine(
            $"[SOULLOCKE-HTTP] SAVE OWN RUN RESPONSE: HTTP {(int)response.StatusCode} " +
            $"{response.ReasonPhrase}, BodyLength={body.Length}, Body='{Truncate(body, 800)}'.");

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Soullocke konnte für den lokalen Spieler nicht gespeichert werden: " +
                $"{(int)response.StatusCode} {body}");
        }

        Console.WriteLine(
            $"[SOULLOCKE-HTTP] SAVE OWN RUN OK: ausschließlich '{localPlayer.PlayerName}'/" +
            $"{_config.PlayerId} wurde mit {encounters.Count} Begegnungen aktualisiert.");
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
        {
            return;
        }

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(_config.SessionId))
            {
                throw new InvalidOperationException(
                    "Der Soullocke-Link enthält keine gültige Session-ID.");
            }

            var session = await LoadSessionMetadataAsync(cancellationToken);
            _sessionGameName = NormalizeSessionGameName(session.Settings.Game);

            ResolvePlayerAssignmentByName(session);
            _config.AuthToken = await AuthenticateAsync(cancellationToken);
            _initialized = true;

            Console.WriteLine(
                $"Soullocke eindeutig über Spielernamen zugeordnet: " +
                $"'{_config.PlayerName}' → {_config.PlayerId}; " +
                $"Session-Spiel='{_sessionGameName}'. " +
                "SoulBuddy schreibt ausschließlich diesen Spieler-Run.");
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<SoullockeSessionResponse> LoadSessionMetadataAsync(
        CancellationToken cancellationToken)
    {
        using var response = await _httpClient.GetAsync(
            ApiBaseUrl + $"session/{Uri.EscapeDataString(_config.SessionId)}",
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Die Soullocke-Sitzung konnte nicht geladen werden: " +
                $"{(int)response.StatusCode} {body}");
        }

        return JsonSerializer.Deserialize<SoullockeSessionResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException(
                "Soullocke hat ungültige Sitzungsdaten zurückgegeben.");
    }

    private void ResolvePlayerAssignmentByName(SoullockeSessionResponse session)
    {
        var entries = new List<(string PlayerId, string TeamName, string PlayerName)>();
        var index = 1;

        foreach (var team in session.Settings.Teams)
        {
            foreach (var playerName in team.Players)
            {
                entries.Add(($"player{index++}", team.Name, playerName));
            }
        }

        var configuredPlayerName = _config.PlayerName.Trim();
        var matches = entries
            .Where(entry => string.Equals(
                entry.PlayerName.Trim(),
                configuredPlayerName,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
        {
            var availableNames = string.Join(", ", entries.Select(entry => entry.PlayerName));
            throw new InvalidOperationException(
                $"Der in SoulBuddy konfigurierte Spielername '{_config.PlayerName}' wurde in " +
                $"Soullocke nicht gefunden. Verfügbare Namen: {availableNames}");
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Der Spielername '{_config.PlayerName}' kommt in Soullocke mehrfach vor. " +
                "Eine sichere Zuordnung ist nicht möglich; es werden keine Daten geschrieben.");
        }

        var local = matches[0];
        _config.PlayerId = local.PlayerId;
        _config.PlayerName = local.PlayerName;
        _config.TeamName = local.TeamName;

        _playerMapping = entries.ToDictionary(
            entry => entry.PlayerId,
            entry => new PlayerMappingEntry
            {
                TeamName = entry.TeamName,
                PlayerName = entry.PlayerName
            },
            StringComparer.OrdinalIgnoreCase);
    }

    private async Task<string> AuthenticateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.AuthToken))
        {
            throw new InvalidOperationException("Bitte gib das Soullocke-Passwort ein.");
        }

        using var response = await _httpClient.PostAsJsonAsync(
            ApiBaseUrl + "session/validate-password",
            new
            {
                sessionId = _config.SessionId,
                password = _config.AuthToken
            },
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Das Soullocke-Passwort konnte nicht geprüft werden: " +
                $"{(int)response.StatusCode} {body}");
        }

        var result = JsonSerializer.Deserialize<SoullockePasswordValidationResponse>(body, JsonOptions);
        if (result is null || !result.IsValid || string.IsNullOrWhiteSpace(result.AuthToken))
        {
            throw new InvalidOperationException(
                "Das eingegebene Soullocke-Passwort ist ungültig.");
        }

        return result.AuthToken;
    }

    private static void LogAllPlayerEncounters(
        IReadOnlyDictionary<string, SoullockeRun> runs,
        IReadOnlyDictionary<string, PlayerMappingEntry> mapping)
    {
        Console.WriteLine("[SOULLOCKE-HTTP] BEGEGNUNGSLISTEN (nur lokaler Spieler wird geschrieben):");

        foreach (var playerRun in runs.OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
        {
            mapping.TryGetValue(playerRun.Key, out var player);
            var playerName = player?.PlayerName ?? "<unbekannt>";

            Console.WriteLine(
                $"[SOULLOCKE-HTTP] PLAYER {playerRun.Key}: Name='{playerName}', " +
                $"Run={playerRun.Value.RunNumber}, Game='{playerRun.Value.GameName}', " +
                $"Status='{playerRun.Value.Status}', Begegnungen={playerRun.Value.Encounters.Count}.");

            foreach (var encounter in playerRun.Value.Encounters
                         .OrderBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase))
            {
                Console.WriteLine(
                    $"[SOULLOCKE-HTTP]   {playerRun.Key}: Ort-Key='{encounter.Key}', " +
                    $"Pokémon=#{encounter.Value.Pokemon}, " +
                    $"Spitzname='{encounter.Value.Nickname ?? "<leer>"}', " +
                    $"Status='{encounter.Value.Status}'.");
            }
        }
    }

    private static string NormalizeSessionGameName(string? gameName)
    {
        var normalized = (gameName ?? string.Empty).Trim().ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(normalized))
        {
            throw new InvalidOperationException(
                "Die Soullocke-Sitzung enthält kein gültiges Spiel.");
        }

        return normalized switch
        {
            "heartgold" or "heart-gold" or "hg" => "heartgold",
            "soulsilver" or "soul-silver" or "ss" => "soulsilver",
            _ => normalized
        };
    }

    private static string Truncate(string value, int maxLength) =>
        value.Length <= maxLength
            ? value
            : value[..maxLength] + "…";
}
