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
    private readonly Dictionary<string, string> _placeholderByInternalLocation =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["Dunkelhöhle"] = "Placeholder 1",
            ["Knofensaturm"] = "Placeholder 2"
        };

    private Dictionary<string, PlayerMappingEntry>? _localPlayerMapping;
    private Dictionary<string, PlayerMappingEntry>? _partnerPlayerMapping;
    private string? _partnerPlayerId;
    private string? _partnerPlayerName;
    private string _sessionGameName = string.Empty;
    private int _localRunNumber;
    private string _localRunStatus = "open";
    private bool _localRunMetadataInitialized;
    private bool _loadedLocalRunRequiresStatusRepair;
    private bool _initialized;

    public SoullockeClient(HttpClient httpClient, AppConfig config)
    {
        _httpClient = httpClient;
        _config = config;
        IsServerSynchronizationHealthy = false;
    }

    public static bool IsServerSynchronizationHealthy { get; private set; }
    public string? PartnerPlayerName => _partnerPlayerName;
    public string SessionGameName => _sessionGameName;

    /// <summary>
    /// Loads the local player's run from Soullocke. SoulBuddy calls this exactly once
    /// during startup. Afterwards the local run is maintained inside SoulBuddy and only
    /// written back to Soullocke.
    /// </summary>
    public async Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken)
    {
        var runs = await LoadAllRunsAsync(cancellationToken);
        if (!runs.TryGetValue(_config.PlayerId, out var run))
        {
            throw new InvalidOperationException(
                $"Der Soullocke-Run für {_config.PlayerName} ({_config.PlayerId}) wurde nicht gefunden.");
        }

        _localRunNumber = run.RunNumber > 0 ? run.RunNumber : _config.RunNumber;
        _localRunStatus = string.IsNullOrWhiteSpace(run.Status) ? "open" : run.Status;
        _localRunMetadataInitialized = true;

        // Older SoulBuddy versions accidentally used the wild opponent's Gen-4
        // LocationMet field as the current HGSS encounter location. Gen-4 IDs 16-45
        // were then mapped to Sinnoh routes 201-230, creating hidden ghost entries
        // such as "Route 221" in an otherwise valid HGSS Soullocke run. Those route
        // numbers cannot exist in HeartGold/SoulSilver, so they are safe to purge.
        var needsRepairSave = RemoveLegacyInvalidHgssRoutes(run) ||
                              _loadedLocalRunRequiresStatusRepair;
        if (needsRepairSave)
        {
            await SaveLocalRunAsync(run.Encounters, cancellationToken);
            _loadedLocalRunRequiresStatusRepair = false;
        }

        return run;
    }

    /// <summary>
    /// Partner data is read-only. No code path in SoulBuddy saves the partner run.
    /// </summary>
    public async Task<SoullockeRun?> LoadPartnerRunAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(_partnerPlayerId) || _partnerPlayerMapping is null)
            return null;

        var runs = await LoadRunsAsync(
            _partnerPlayerId,
            _partnerPlayerMapping,
            cancellationToken);

        return runs.TryGetValue(_partnerPlayerId, out var run) ? run : null;
    }

    public async Task<IReadOnlyDictionary<string, SoullockeRun>> LoadAllRunsAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var mapping = _localPlayerMapping
            ?? throw new InvalidOperationException("Der lokale Soullocke-Spieler wurde nicht initialisiert.");

        return await LoadRunsAsync(_config.PlayerId, mapping, cancellationToken);
    }

    private async Task<IReadOnlyDictionary<string, SoullockeRun>> LoadRunsAsync(
        string playerId,
        Dictionary<string, PlayerMappingEntry> mapping,
        CancellationToken cancellationToken)
    {
        var request = new LoadRunsRequest
        {
            SessionId = _config.SessionId,
            Players =
            [
                new LoadRunPlayer
                {
                    PlayerId = playerId,
                    RunNumber = _config.RunNumber
                }
            ],
            PlayerMapping = mapping
        };

        using var response = await SendWithTimeoutAsync(
            token => _httpClient.PostAsJsonAsync(
                ApiBaseUrl + "game/batchLoadRuns",
                request,
                token),
            "Soullocke-Run laden",
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Soullocke konnte nicht geladen werden: {(int)response.StatusCode} {body}");
        }

        var result = JsonSerializer.Deserialize<BatchLoadResponse>(body, JsonOptions)?.PlayerData
            ?? throw new InvalidOperationException("Soullocke hat ungültige Run-Daten zurückgegeben.");

        if (result.TryGetValue(playerId, out var run))
        {
            if (string.Equals(playerId, _config.PlayerId, StringComparison.OrdinalIgnoreCase) &&
                run.Encounters.Values.Any(encounter => IsLegacySoulBuddyNotCaughtStatus(encounter.Status)))
            {
                // SoulBuddy briefly emitted two status spellings that Soullocke does not
                // recognize. Normalize them locally, then rewrite the run using the exact
                // value used by Soullocke itself: "not-catched".
                _loadedLocalRunRequiresStatusRepair = true;
            }

            NormalizeLoadedEncounterKeys(run);
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
        return Task.FromResult(false);
    }

    private async Task SaveLocalRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        if (!_localRunMetadataInitialized)
        {
            throw new InvalidOperationException(
                "Der eigene Soullocke-Run wurde noch nicht initial eingelesen und darf noch nicht gespeichert werden.");
        }

        var mapping = _localPlayerMapping
            ?? throw new InvalidOperationException("Der lokale Soullocke-Spieler wurde nicht initialisiert.");
        var localPlayer = mapping[_config.PlayerId];
        var apiEncounters = ConvertEncounterKeysForSoullocke(encounters);

        var query =
            $"game/saveRun?sessionId={Uri.EscapeDataString(_config.SessionId)}&" +
            $"teamName={Uri.EscapeDataString(localPlayer.TeamName)}&" +
            $"playerName={Uri.EscapeDataString(localPlayer.PlayerName)}&" +
            $"authToken={Uri.EscapeDataString(_config.AuthToken)}";

        var request = new SaveRunRequest
        {
            PlayerId = _config.PlayerId,
            RunNumber = _localRunNumber > 0 ? _localRunNumber : _config.RunNumber,
            GameName = _sessionGameName,
            Status = string.IsNullOrWhiteSpace(_localRunStatus) ? "open" : _localRunStatus,
            Encounters = apiEncounters
        };

        using var response = await SendWithTimeoutAsync(
            token => _httpClient.PostAsJsonAsync(ApiBaseUrl + query, request, token),
            "eigenen Soullocke-Run speichern",
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Soullocke konnte für den lokalen Spieler nicht gespeichert werden: " +
                $"{(int)response.StatusCode} {body}");
        }
    }

    private bool RemoveLegacyInvalidHgssRoutes(SoullockeRun run)
    {
        if (_sessionGameName is not "heartgold" and not "soulsilver")
            return false;

        var invalidKeys = run.Encounters.Keys
            .Where(IsInvalidHgssSinnohRoute)
            .ToArray();

        foreach (var key in invalidKeys)
        {
            run.Encounters.Remove(key);
            Console.WriteLine(
                $"Legacy-SoulBuddy-Encounter aus Soullocke entfernt: {key} " +
                "(ungültige HGSS-Route aus früherer Ortserkennung).");
        }

        return invalidKeys.Length > 0;
    }

    private static bool IsInvalidHgssSinnohRoute(string location)
    {
        var trimmed = location.Trim();
        if (!trimmed.StartsWith("Route ", StringComparison.OrdinalIgnoreCase))
            return false;

        return int.TryParse(trimmed[6..], out var route) && route is >= 201 and <= 230;
    }

    private Dictionary<string, SoullockeEncounter> ConvertEncounterKeysForSoullocke(
        IReadOnlyDictionary<string, SoullockeEncounter> encounters)
    {
        var converted = new Dictionary<string, SoullockeEncounter>(StringComparer.OrdinalIgnoreCase);
        var usedPlaceholders = new HashSet<string>(
            _placeholderByInternalLocation.Values,
            StringComparer.OrdinalIgnoreCase);

        foreach (var key in encounters.Keys)
        {
            var trimmed = key.Trim();
            if (IsPlaceholder(trimmed))
                usedPlaceholders.Add(trimmed);
        }

        foreach (var pair in encounters)
        {
            var internalLocation = NormalizeInternalLocation(pair.Key);
            var apiLocation = ResolveSoullockeLocation(internalLocation, usedPlaceholders);
            converted[apiLocation] = new SoullockeEncounter
            {
                Pokemon = pair.Value.Pokemon,
                Nickname = pair.Value.Nickname,
                Status = ToSoullockeStatus(pair.Value.Status)
            };
        }

        return converted;
    }

    private string ResolveSoullockeLocation(
        string internalLocation,
        HashSet<string> usedPlaceholders)
    {
        if (IsDirectSoullockeLocation(internalLocation))
            return internalLocation;

        if (_placeholderByInternalLocation.TryGetValue(internalLocation, out var existing))
        {
            usedPlaceholders.Add(existing);
            return existing;
        }

        for (var number = 3; number <= 9; number++)
        {
            var placeholder = $"Placeholder {number}";
            if (!usedPlaceholders.Add(placeholder))
                continue;

            _placeholderByInternalLocation[internalLocation] = placeholder;
            return placeholder;
        }

        throw new InvalidOperationException(
            $"Für den nicht zuordenbaren Fangort '{internalLocation}' ist kein freier " +
            "Soullocke-Platzhalter mehr verfügbar. Unterstützt werden Placeholder 1 bis 9.");
    }

    private void NormalizeLoadedEncounterKeys(SoullockeRun run)
    {
        foreach (var pair in run.Encounters.ToArray())
        {
            pair.Value.Status = FromSoullockeStatus(pair.Value.Status);

            var internalLocation = ResolveInternalLocationFromSoullocke(pair.Key);
            if (string.Equals(pair.Key, internalLocation, StringComparison.Ordinal))
                continue;

            run.Encounters.Remove(pair.Key);
            run.Encounters[internalLocation] = pair.Value;
        }
    }

    private string ResolveInternalLocationFromSoullocke(string location)
    {
        var trimmed = location.Trim();
        var knownInternal = _placeholderByInternalLocation
            .FirstOrDefault(pair => string.Equals(
                pair.Value,
                trimmed,
                StringComparison.OrdinalIgnoreCase))
            .Key;

        return string.IsNullOrWhiteSpace(knownInternal)
            ? NormalizeInternalLocation(trimmed)
            : knownInternal;
    }

    private static string NormalizeInternalLocation(string location) =>
        location.Trim() switch
        {
            "Finsterhöhle" or "Dark Cave" or "Placeholder 1" => "Dunkelhöhle",
            "Sprout Tower" or "Placeholder 2" or "" => "Knofensaturm",
            _ => location.Trim()
        };

    private static bool IsLegacySoulBuddyNotCaughtStatus(string? status) =>
        (status ?? string.Empty).Trim().ToLowerInvariant() is "notcaught" or "not-caught";

    private static string ToSoullockeStatus(string? status) =>
        (status ?? "alive").Trim().ToLowerInvariant() switch
        {
            "fainted" => "fainted",
            "notcaught" or "not-caught" or "not-catched" => "not-catched",
            "brofailed" or "bro-failed" => "bro-failed",
            "boxed" or "box" => "boxed",
            _ => "alive"
        };

    private static string FromSoullockeStatus(string? status) =>
        (status ?? "alive").Trim().ToLowerInvariant() switch
        {
            "fainted" => "fainted",
            "notcaught" or "not-caught" or "not-catched" => "notcaught",
            "brofailed" or "bro-failed" => "brofailed",
            "boxed" or "box" => "boxed",
            _ => "alive"
        };

    private static bool IsDirectSoullockeLocation(string location)
    {
        if (string.Equals(location, "Starter", StringComparison.OrdinalIgnoreCase))
            return true;

        if (IsPlaceholder(location))
            return true;

        return location.StartsWith("Route ", StringComparison.OrdinalIgnoreCase) &&
               int.TryParse(location[6..], out _);
    }

    private static bool IsPlaceholder(string location) =>
        location.StartsWith("Placeholder ", StringComparison.OrdinalIgnoreCase) &&
        int.TryParse(location[12..], out var number) &&
        number is >= 1 and <= 9;

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

            var session = await LoadSessionMetadataAsync(cancellationToken);
            _sessionGameName = NormalizeSessionGameName(session.Settings.Game);
            ResolvePlayerAssignmentByName(session);
            _config.AuthToken = await AuthenticateAsync(cancellationToken);
            _initialized = true;
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

        // SoulBuddy's SoulLink partner is the other unique player in the session.
        // The Soullocke UI may place the two players in different teams, so team
        // membership must never be used to decide who the partner is.
        var partners = entries
            .Where(entry => !string.Equals(
                entry.PlayerId,
                local.PlayerId,
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (partners.Length == 1)
        {
            var partner = partners[0];
            _partnerPlayerId = partner.PlayerId;
            _partnerPlayerName = partner.PlayerName;
            _partnerPlayerMapping = new Dictionary<string, PlayerMappingEntry>(StringComparer.OrdinalIgnoreCase)
            {
                [partner.PlayerId] = new PlayerMappingEntry
                {
                    TeamName = partner.TeamName,
                    PlayerName = partner.PlayerName
                }
            };
        }
        else
        {
            _partnerPlayerId = null;
            _partnerPlayerName = null;
            _partnerPlayerMapping = null;
        }
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
            var response = await send(timeout.Token);
            IsServerSynchronizationHealthy = response.IsSuccessStatusCode;
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            IsServerSynchronizationHealthy = false;
            throw new TimeoutException(
                $"Zeitüberschreitung beim Vorgang '{operation}' nach " +
                $"{RequestTimeout.TotalSeconds:0} Sekunden.");
        }
        catch
        {
            IsServerSynchronizationHealthy = false;
            throw;
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
}
