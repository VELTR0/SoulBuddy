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
        using var response = await _httpClient.PostAsJsonAsync(ApiBaseUrl + "game/batchLoadRuns", request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
            throw new HttpRequestException($"Soullocke konnte nicht geladen werden: {(int)response.StatusCode} {body}");
        return JsonSerializer.Deserialize<BatchLoadResponse>(body, JsonOptions)?.PlayerData
            ?? throw new InvalidOperationException("Soullocke hat ungültige Run-Daten zurückgegeben.");
    }

    public Task SaveRunAsync(Dictionary<string, SoullockeEncounter> encounters, CancellationToken cancellationToken) =>
        SaveRunForPlayerAsync(_config.PlayerId, encounters, cancellationToken);

    public async Task<bool> MarkLinkedPartnerBroFailedAsync(string location, CancellationToken cancellationToken)
    {
        var runs = await LoadAllRunsAsync(cancellationToken);
        foreach (var pair in runs)
        {
            if (string.Equals(pair.Key, _config.PlayerId, StringComparison.OrdinalIgnoreCase)) continue;
            if (!pair.Value.Encounters.TryGetValue(location, out var partnerEncounter)) continue;
            if (string.Equals(partnerEncounter.Status, "brofailed", StringComparison.OrdinalIgnoreCase)) return false;
            partnerEncounter.Status = "brofailed";
            await SaveRunForPlayerAsync(pair.Key, pair.Value.Encounters, cancellationToken);
            Console.WriteLine($"Soullocke: Partner-Begegnung „{location}“ einmalig auf Bro-Failed gesetzt.");
            return true;
        }
        return false;
    }

    private async Task SaveRunForPlayerAsync(string playerId, Dictionary<string, SoullockeEncounter> encounters, CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var mapping = _playerMapping ?? throw new InvalidOperationException("Die Soullocke-Spielerzuordnung wurde nicht initialisiert.");
        if (!mapping.TryGetValue(playerId, out var player)) throw new InvalidOperationException($"Soullocke-Spieler {playerId} ist unbekannt.");
        var query = $"game/saveRun?sessionId={Uri.EscapeDataString(_config.SessionId)}&" +
                    $"teamName={Uri.EscapeDataString(player.TeamName)}&" +
                    $"playerName={Uri.EscapeDataString(player.PlayerName)}&" +
                    $"authToken={Uri.EscapeDataString(_config.AuthToken)}";
        var request = new SaveRunRequest { PlayerId = playerId, RunNumber = _config.RunNumber, Encounters = encounters };
        using var response = await _httpClient.PostAsJsonAsync(ApiBaseUrl + query, request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode) throw new HttpRequestException($"Soullocke konnte nicht gespeichert werden: {(int)response.StatusCode} {body}");
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
            ResolvePlayerAssignment(session);
            _config.AuthToken = await AuthenticateAsync(cancellationToken);
            _initialized = true;
            Console.WriteLine($"Soullocke zugeordnet: {_config.PlayerName} → {_config.PlayerId} / {_config.TeamName}");
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
}
