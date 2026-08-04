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
    [JsonPropertyName("playerData")]
    public Dictionary<string, SoullockeRun> PlayerData { get; init; } = [];

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

    [JsonPropertyName("encounters")]
    public required Dictionary<string, SoullockeEncounter> Encounters { get; init; }
}
