namespace SoulBuddy.Models;

public sealed class AppConfig
{
    public required string PartyJsonPath { get; init; }
    public string SessionId { get; init; } = string.Empty;
    public string PlayerId { get; init; } = "player1";
    public string TeamName { get; init; } = string.Empty;
    public string PlayerName { get; init; } = string.Empty;
    public string AuthToken { get; init; } = string.Empty;

    public int RunNumber { get; init; } = 1;
    public int PollIntervalMilliseconds { get; init; } = 1000;
    public bool DryRun { get; init; } = true;
    public bool SoullockeEnabled { get; init; }
}
