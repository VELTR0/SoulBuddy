using System.Net.Http.Json;
using System.Text.Json;
using SoulSync.Models;

namespace SoulSync.Services;

public sealed class SoullockeClient
{
    private const string BaseUrl = "https://soullocke.com:7001/api/game/";

    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;

    public SoullockeClient(HttpClient httpClient, AppConfig config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    public async Task<SoullockeRun> LoadRunAsync(
        CancellationToken cancellationToken)
    {
        var request = new LoadRunsRequest
        {
            SessionId = _config.SessionId,
            Players =
            [
                new LoadRunPlayer
                {
                    PlayerId = "player1",
                    RunNumber = _config.RunNumber
                },
                new LoadRunPlayer
                {
                    PlayerId = "player2",
                    RunNumber = _config.RunNumber
                }
            ],
            PlayerMapping = new Dictionary<string, PlayerMappingEntry>
            {
                ["player1"] = new()
                {
                    TeamName = _config.PlayerId == "player1"
                        ? _config.TeamName
                        : "Unknown",
                    PlayerName = _config.PlayerId == "player1"
                        ? _config.PlayerName
                        : "Unknown"
                },
                ["player2"] = new()
                {
                    TeamName = _config.PlayerId == "player2"
                        ? _config.TeamName
                        : "Unknown",
                    PlayerName = _config.PlayerId == "player2"
                        ? _config.PlayerName
                        : "Unknown"
                }
            }
        };

        using var response = await _httpClient.PostAsJsonAsync(
            BaseUrl + "batchLoadRuns",
            request,
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Soullocke konnte nicht geladen werden: " +
                $"{(int)response.StatusCode} {body}");
        }

        var result = JsonSerializer.Deserialize<BatchLoadResponse>(
            body,
            new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

        if (result is null ||
            !result.PlayerData.TryGetValue(_config.PlayerId, out var run))
        {
            throw new InvalidOperationException(
                $"Der Run für {_config.PlayerId} wurde nicht gefunden.");
        }

        return run;
    }

    public async Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken)
    {
        var query =
            $"saveRun?" +
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
            BaseUrl + query,
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
}