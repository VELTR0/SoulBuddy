using System.Text.Json.Serialization;

namespace SoulBuddy.Models;

public sealed class SoullockeSessionResponse
{
    [JsonPropertyName("id")]
    public string Id { get; init; } = string.Empty;

    [JsonPropertyName("settings")]
    public SoullockeSessionSettings Settings { get; init; } = new();
}

public sealed class SoullockeSessionSettings
{
    [JsonPropertyName("teams")]
    public List<SoullockeTeam> Teams { get; init; } = [];

    [JsonPropertyName("game")]
    public string Game { get; init; } = string.Empty;
}

public sealed class SoullockeTeam
{
    [JsonPropertyName("name")]
    public string Name { get; init; } = string.Empty;

    [JsonPropertyName("players")]
    public List<string> Players { get; init; } = [];
}

public sealed class SoullockePasswordValidationResponse
{
    [JsonPropertyName("isValid")]
    public bool IsValid { get; init; }

    [JsonPropertyName("authToken")]
    public string AuthToken { get; init; } = string.Empty;
}

public sealed class BatchLoadResponse
{
    private Dictionary<string, SoullockeRun> _playerData = [];

    [JsonPropertyName("playerData")]
    public Dictionary<string, SoullockeRun> PlayerData
    {
        get => _playerData;
        init
        {
            _playerData = value ?? [];
            SoullockePartnerCatchObserver.ObserveLoadedRuns(_playerData);
        }
    }

    [JsonPropertyName("errors")]
    public List<object> Errors { get; init; } = [];
}

public sealed class SoullockeRun
{
    [JsonPropertyName("playerId")]
    public string PlayerId { get; init; } = string.Empty;

    [JsonPropertyName("runNumber")]
    public int RunNumber { get; init; }

    [JsonPropertyName("gameName")]
    public string GameName { get; init; } = string.Empty;

    [JsonPropertyName("status")]
    public string Status { get; init; } = string.Empty;

    [JsonPropertyName("encounters")]
    public Dictionary<string, SoullockeEncounter> Encounters { get; init; } = [];
}

public sealed class SoullockeEncounter
{
    [JsonPropertyName("pokemon")]
    public int Pokemon { get; set; }

    [JsonPropertyName("nickname")]
    public string? Nickname { get; set; }

    [JsonPropertyName("status")]
    public string Status { get; set; } = "alive";
}

public sealed record SoullockePartnerCatchDetected(
    int Pokemon,
    string? Nickname,
    string Location);

public sealed record SoullockePartnerCatchFailedDetected(
    int Pokemon,
    string? Nickname,
    string Location);

/// <summary>
/// Observes the batch-load responses that SoulBuddy already performs. The first
/// response for each player establishes a baseline. Later additions are surfaced as
/// successful or failed catches. Partner boxing is handled exclusively by SyncService
/// through the alive-to-boxed SoulLocke status transition.
/// </summary>
public static class SoullockePartnerCatchObserver
{
    private static readonly object Sync = new();
    private static readonly Dictionary<string, Dictionary<string, EncounterSnapshot>> Snapshots =
        new(StringComparer.OrdinalIgnoreCase);

    private static Action<SoullockePartnerCatchDetected>? _handler;
    private static Action<SoullockePartnerCatchFailedDetected>? _failureHandler;

    public static void ResetAndSetHandler(Action<SoullockePartnerCatchDetected> handler)
    {
        lock (Sync)
        {
            Snapshots.Clear();
            _handler = handler;
            _failureHandler = null;
        }
    }

    public static void SetFailureHandler(Action<SoullockePartnerCatchFailedDetected> handler)
    {
        lock (Sync)
            _failureHandler = handler;
    }

    public static void ObserveLoadedRuns(IReadOnlyDictionary<string, SoullockeRun> runs)
    {
        var caught = new List<SoullockePartnerCatchDetected>();
        var failed = new List<SoullockePartnerCatchFailedDetected>();
        Action<SoullockePartnerCatchDetected>? handler;
        Action<SoullockePartnerCatchFailedDetected>? failureHandler;

        lock (Sync)
        {
            foreach (var player in runs)
            {
                var current = BuildSnapshot(player.Value);

                if (Snapshots.TryGetValue(player.Key, out var previous))
                {
                    foreach (var encounter in current)
                    {
                        if (encounter.Value.Pokemon <= 0)
                            continue;

                        var existedBefore = previous.TryGetValue(encounter.Key, out var oldEncounter) &&
                                           oldEncounter.Pokemon > 0;
                        if (existedBefore)
                            continue;

                        if (encounter.Value.Status is "alive" or "boxed")
                        {
                            caught.Add(new SoullockePartnerCatchDetected(
                                encounter.Value.Pokemon,
                                encounter.Value.Nickname,
                                encounter.Value.Location));
                            continue;
                        }

                        if (encounter.Value.Status == "notcaught")
                        {
                            failed.Add(new SoullockePartnerCatchFailedDetected(
                                encounter.Value.Pokemon,
                                encounter.Value.Nickname,
                                encounter.Value.Location));
                        }
                    }
                }

                Snapshots[player.Key] = current;
            }

            handler = _handler;
            failureHandler = _failureHandler;
        }

        if (handler is not null)
        {
            foreach (var encounter in caught)
                handler(encounter);
        }

        if (failureHandler is not null)
        {
            foreach (var encounter in failed)
                failureHandler(encounter);
        }
    }

    private static Dictionary<string, EncounterSnapshot> BuildSnapshot(SoullockeRun run)
    {
        var result = new Dictionary<string, EncounterSnapshot>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in run.Encounters)
        {
            var location = pair.Key.Trim();
            result[NormalizeLocation(location)] = new EncounterSnapshot(
                pair.Value.Pokemon,
                pair.Value.Nickname,
                NormalizeStatus(pair.Value.Status),
                location);
        }

        return result;
    }

    private static string NormalizeLocation(string value) =>
        new(value
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private static string NormalizeStatus(string? status) =>
        (status ?? "alive").Trim().ToLowerInvariant() switch
        {
            "box" or "boxed" => "boxed",
            "alive" => "alive",
            "notcaught" or "not-caught" or "not-catched" => "notcaught",
            "fainted" => "fainted",
            "brofailed" or "bro-failed" => "brofailed",
            _ => "alive"
        };

    private sealed record EncounterSnapshot(
        int Pokemon,
        string? Nickname,
        string Status,
        string Location);
}

public sealed class LoadRunsRequest
{
    [JsonPropertyName("players")]
    public required List<LoadRunPlayer> Players { get; init; }

    [JsonPropertyName("sessionId")]
    public required string SessionId { get; init; }

    [JsonPropertyName("playerMapping")]
    public required Dictionary<string, PlayerMappingEntry> PlayerMapping { get; init; }
}

public sealed class LoadRunPlayer
{
    [JsonPropertyName("playerId")]
    public required string PlayerId { get; init; }

    [JsonPropertyName("runNumber")]
    public int RunNumber { get; init; }
}

public sealed class PlayerMappingEntry
{
    [JsonPropertyName("TeamName")]
    public required string TeamName { get; init; }

    [JsonPropertyName("PlayerName")]
    public required string PlayerName { get; init; }
}

public sealed class SaveRunRequest
{
    [JsonPropertyName("playerId")]
    public required string PlayerId { get; init; }

    [JsonPropertyName("runNumber")]
    public int RunNumber { get; init; }

    [JsonPropertyName("gameName")]
    public required string GameName { get; init; }

    [JsonPropertyName("status")]
    public required string Status { get; init; }

    [JsonPropertyName("encounters")]
    public required Dictionary<string, SoullockeEncounter> Encounters { get; init; }
}
