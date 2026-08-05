using System.Net.Http.Json;
using System.Text.Json;
using SoulBuddy.Models;

namespace SoulBuddy.Services;

public sealed class SoullockeClient
{
    private const string ApiBaseUrl = "https://soullocke.com:7001/api/";
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    private Dictionary<string, PlayerMappingEntry>? _localPlayerMapping;
    private string _sessionGameName = string.Empty;
    private bool _initialized;

    public SoullockeClient(HttpClient httpClient, AppConfig config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    public async Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken)
    {
        var runs = await LoadAllRunsAsync(cancellationToken);
        return runs.TryGetValue(_config.PlayerId, out var run)
            ? run
            : throw new InvalidOperationException(
                $"Der Soullocke-Run für {_config.PlayerName} ({_config.PlayerId}) wurde nicht gefunden.");
    }

    public async Task<IReadOnlyDictionary<string, SoullockeRun>> LoadAllRunsAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var mapping = _localPlayerMapping
            ?? throw new InvalidOperationException("Der lokale Soullocke-Spieler wurde nicht initialisiert.");

        var request = new LoadRunsRequest
        {
            SessionId = _config.SessionId,
            Players =
            [
                new LoadRunPlayer
                {
                    PlayerId = _config.PlayerId,
                    RunNumber = _config.RunNumber
                }
            ],
            PlayerMapping = mapping
        };

        Console.WriteLine(
            $"[SOULLOCKE-HTTP] LOAD OWN RUN START: Session='{_config.SessionId}', " +
            $"Spieler='{_config.PlayerName}'/{_config.PlayerId}, Run={_config.RunNumber}.");

        using var response = await SendWithTimeoutAsync(
            token => _httpClient.PostAsJsonAsync(
                ApiBaseUrl + "game/batchLoadRuns",
                request,
                token),
            "Soullocke-Run laden",
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Console.WriteLine(
            $"[SOULLOCKE-HTTP] LOAD OWN RUN RESPONSE: HTTP {(int)response.StatusCode} " +
            $"{response.ReasonPhrase}, BodyLength={body.Length}.");

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Soullocke konnte nicht geladen werden: {(int)response.StatusCode} {body}");
        }

        var result = JsonSerializer.Deserialize<BatchLoadResponse>(body, JsonOptions)?.PlayerData
            ?? throw new InvalidOperationException("Soullocke hat ungültige Run-Daten zurückgegeben.");

        if (result.TryGetValue(_config.PlayerId, out var run))
        {
            Console.WriteLine(
                $"[SOULLOCKE-HTTP] LOAD OWN RUN OK: '{_config.PlayerName}'/{_config.PlayerId}=" +
                $"{run.Encounters.Count} Begegnungen.");
        }

        return result;
    }

    public Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken) =>
        SaveLocalRunAsync(encounters, cancellationToken);

    public Task<bool> MarkLinkedPartnerBroFailedAsync(
        string location,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        Console.WriteLine(
            $"[SOULLOCKE-SYNC] Partnerstatus für Ort '{location}' wird nicht geschrieben. " +
            "SoulBuddy aktualisiert ausschließlich den eigenen Run.");
        return Task.FromResult(false);
    }

    private async Task SaveLocalRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var mapping = _localPlayerMapping
            ?? throw new InvalidOperationException("Der lokale Soullocke-Spieler wurde nicht initialisiert.");
        var localPlayer = mapping[_config.PlayerId];
        var currentRun = await LoadRunAsync(cancellationToken);

        var query =
            $"game/saveRun?sessionId={Uri.EscapeDataString(_config.SessionId)}&" +
            $"teamName={Uri.EscapeDataString(localPlayer.TeamName)}&" +
            $"playerName={Uri.EscapeDataString(localPlayer.PlayerName)}&" +
            $"authToken={Uri.EscapeDataString(_config.AuthToken)}";

        var request = new SaveRunRequest
        {
            PlayerId = _config.PlayerId,
            RunNumber = currentRun.RunNumber > 0 ? currentRun.RunNumber : _config.RunNumber,
            GameName = _sessionGameName,
            Status = string.IsNullOrWhiteSpace(currentRun.Status) ? "open" : currentRun.Status,
            Encounters = encounters
        };

        Console.WriteLine(
            $"[SOULLOCKE-HTTP] SAVE OWN RUN START: Spieler='{localPlayer.PlayerName}'/" +
            $"{_config.PlayerId}, Begegnungen={encounters.Count}: " +
            string.Join(", ", encounters.Select(pair =>
                $"'{pair.Key}'=#{pair.Value.Pokemon}/{pair.Value.Status}")));

        using var response = await SendWithTimeoutAsync(
            token => _httpClient.PostAsJsonAsync(ApiBaseUrl + query, request, token),
            "eigenen Soullocke-Run speichern",
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        Console.WriteLine(
            $"[SOULLOCKE-HTTP] SAVE OWN RUN RESPONSE: HTTP {(int)response.StatusCode} " +
            $"{response.ReasonPhrase}, Body='{Truncate(body, 800)}'.");

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Soullocke konnte für den lokalen Spieler nicht gespeichert werden: " +
                $"{(int)response.StatusCode} {body}");
        }

        Console.WriteLine(
            $"[SOULLOCKE-HTTP] SAVE OWN RUN OK: ausschließlich '{localPlayer.PlayerName}'/" +
            $"{_config.PlayerId} wurde aktualisiert.");
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;

            if (string.IsNullOrWhiteSpace(_config.SessionId))
                throw new InvalidOperationException("Der Soullocke-Link enthält keine gültige Session-ID.");

            Console.WriteLine("[SOULLOCKE-INIT] Lade Sitzungsdaten …");
            var session = await LoadSessionMetadataAsync(cancellationToken);
            Console.WriteLine("[SOULLOCKE-INIT] Sitzungsdaten geladen.");

            _sessionGameName = NormalizeSessionGameName(session.Settings.Game);
            ResolvePlayerAssignmentByName(session);

            Console.WriteLine(
                $"[SOULLOCKE-INIT] Spielername eindeutig zugeordnet: " +
                $"'{_config.PlayerName}' → {_config.PlayerId}. Authentifizierung läuft …");

            _config.AuthToken = await AuthenticateAsync(cancellationToken);
            Console.WriteLine("[SOULLOCKE-INIT] Authentifizierung erfolgreich.");

            _initialized = true;
            Console.WriteLine(
                $"Soullocke eindeutig über Spielernamen zugeordnet: '{_config.PlayerName}' → " +
                $"{_config.PlayerId}; Session-Spiel='{_sessionGameName}'. " +
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
        using var response = await SendWithTimeoutAsync(
            token => _httpClient.GetAsync(
                ApiBaseUrl + $"session/{Uri.EscapeDataString(_config.SessionId)}",
                token),
            "Soullocke-Sitzungsdaten laden",
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Die Soullocke-Sitzung konnte nicht geladen werden: " +
                $"{(int)response.StatusCode} {body}");
        }

        return JsonSerializer.Deserialize<SoullockeSessionResponse>(body, JsonOptions)
            ?? throw new InvalidOperationException("Soullocke hat ungültige Sitzungsdaten zurückgegeben.");
    }

    private void ResolvePlayerAssignmentByName(SoullockeSessionResponse session)
    {
        var entries = new List<(string PlayerId, string TeamName, string PlayerName)>();
        var index = 1;

        foreach (var team in session.Settings.Teams)
        {
            foreach (var playerName in team.Players)
                entries.Add(($"player{index++}", team.Name, playerName));
        }

        var matches = entries
            .Where(entry => string.Equals(
                entry.PlayerName.Trim(),
                _config.PlayerName.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
        {
            throw new InvalidOperationException(
                $"Der in SoulBuddy konfigurierte Spielername '{_config.PlayerName}' wurde in " +
                $"Soullocke nicht gefunden. Verfügbare Namen: " +
                string.Join(", ", entries.Select(entry => entry.PlayerName)));
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Der Spielername '{_config.PlayerName}' kommt in Soullocke mehrfach vor. " +
                "Eine sichere Zuordnung ist nicht möglich.");
        }

        var local = matches[0];
        _config.PlayerId = local.PlayerId;
        _config.PlayerName = local.PlayerName;
        _config.TeamName = local.TeamName;

        _localPlayerMapping = new Dictionary<string, PlayerMappingEntry>(StringComparer.OrdinalIgnoreCase)
        {
            [_config.PlayerId] = new PlayerMappingEntry
            {
                TeamName = local.TeamName,
                PlayerName = local.PlayerName
            }
        };
    }

    private async Task<string> AuthenticateAsync(CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_config.AuthToken))
            throw new InvalidOperationException("Bitte gib das Soullocke-Passwort ein.");

        using var response = await SendWithTimeoutAsync(
            token => _httpClient.PostAsJsonAsync(
                ApiBaseUrl + "session/validate-password",
                new
                {
                    sessionId = _config.SessionId,
                    password = _config.AuthToken
                },
                token),
            "Soullocke-Passwort prüfen",
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
            throw new InvalidOperationException("Das eingegebene Soullocke-Passwort ist ungültig.");

        return result.AuthToken;
    }

    private static async Task<HttpResponseMessage> SendWithTimeoutAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        string operation,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);

        try
        {
            return await send(timeout.Token);
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"Zeitüberschreitung beim Vorgang '{operation}' nach " +
                $"{RequestTimeout.TotalSeconds:0} Sekunden.");
        }
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
