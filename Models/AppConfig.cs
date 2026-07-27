namespace SoulBuddy.Models;

public sealed class AppConfig
{
    public required string PartyJsonPath { get; init; }
    public required string SessionId { get; init; }
    public required string PlayerId { get; init; }
    public required string TeamName { get; init; }
    public required string PlayerName { get; init; }
    public required string AuthToken { get; init; }

    public int RunNumber { get; init; } = 1;
    public int PollIntervalMilliseconds { get; init; } = 1000;
    public bool DryRun { get; init; } = true;
}