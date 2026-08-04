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
    private bool _initialized;

    public SoullockeClient(HttpClient httpClient, AppConfig config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    public async Task<SoullockeRun> LoadRunAsync(
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var mapping = _playerMapping
            ?? throw new InvalidOperationException(
                "Die Soullocke-Spielerzuordnung wurde nicht initialisiert.");

        var request = new LoadRunsRequest
        {
            SessionId = _config.SessionId,
            Players = mapping.Keys
                .Select(playerId => new LoadRunPlayer
                {
                    PlayerId = playerId,
                    RunNumber = _config.RunNumber
                })
                .ToList(),
            PlayerMapping = mapping
        };

        using var response = await _httpClient.PostAsJsonAsync(
            ApiBaseUrl + "game/batchLoadRuns",
            request,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Soullocke konnte nicht geladen werden: " +
                $"{(int)response.StatusCode} {body}");
        }

        var result = JsonSerializer.Deserialize<BatchLoadResponse>(body, JsonOptions);
        if (result is null ||
            !result.PlayerData.TryGetValue(_config.PlayerId, out var run))
        {
            throw new InvalidOperationException(
                $"Der Soullocke-Run für {_config.PlayerName} " +
                $"({_config.PlayerId}) wurde nicht gefunden.");
        }

        return run;
    }

    public async Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var query =
            $"game/saveRun?" +
            $"sessionId={Uri.EscapeDataString(_config.SessionId)}&" +
            $"teamName={Uri.EscapeDataString(_config.TeamName)}&" +
            $"playerName={Uri.EscapeDataString(_config.PlayerName)}&" +
            $"authToken={Uri.EscapeDataString(_config.AuthToken)}";

        var request = new SaveRunRequest
        {
            PlayerId = _config.PlayerId,
            RunNumber = _config.RunNumber,
            Encounters = encounters
        };

        using var response = await _httpClient.PostAsJsonAsync(
            ApiBaseUrl + query,
            request,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Soullocke konnte nicht gespeichert werden: " +
                $"{(int)response.StatusCode} {body}");
        }
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
            ResolvePlayerAssignment(session);
            _config.AuthToken = await AuthenticateAsync(cancellationToken);
            _initialized = true;

            Console.WriteLine(
                $"Soullocke zugeordnet: {_config.PlayerName} → " +
                $"{_config.PlayerId} / {_config.TeamName}");
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

    private void ResolvePlayerAssignment(SoullockeSessionResponse session)
    {
        var entries = new List<(string PlayerId, string TeamName, string PlayerName)>();
        var index = 1;

        foreach (var team in session.Settings.Teams)
        {
            foreach (var player in team.Players)
            {
                entries.Add(($"player{index}", team.Name, player));
                index++;
            }
        }

        if (entries.Count == 0)
        {
            throw new InvalidOperationException(
                "In der Soullocke-Sitzung wurden keine Spieler gefunden.");
        }

        var local = entries.FirstOrDefault(entry => string.Equals(
            entry.PlayerName.Trim(),
            _config.PlayerName.Trim(),
            StringComparison.OrdinalIgnoreCase));

        if (string.IsNullOrWhiteSpace(local.PlayerId))
        {
            var availableNames = string.Join(", ", entries.Select(entry => entry.PlayerName));
            throw new InvalidOperationException(
                $"Der Spielername „{_config.PlayerName}“ wurde in Soullocke nicht gefunden. " +
                $"Verfügbare Namen: {availableNames}");
        }

        _config.PlayerId = local.PlayerId;
        _config.TeamName = local.TeamName;
        _config.PlayerName = local.PlayerName;
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
            throw new InvalidOperationException(
                "Bitte gib das Soullocke-Passwort ein.");
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

        var result = JsonSerializer.Deserialize<SoullockePasswordValidationResponse>(
            body,
            JsonOptions);

        if (result is null || !result.IsValid || string.IsNullOrWhiteSpace(result.AuthToken))
        {
            throw new InvalidOperationException(
                "Das eingegebene Soullocke-Passwort ist ungültig.");
        }

        return result.AuthToken;
    }
}
