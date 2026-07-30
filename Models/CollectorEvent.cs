namespace SoulBuddy.Models;

public sealed class CollectorEvent
{
    public int ProtocolVersion { get; init; }

    public string Type { get; init; } = string.Empty;

    public string? Game { get; init; }

    public long Timestamp { get; init; }
}