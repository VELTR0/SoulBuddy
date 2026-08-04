using System.Text.Json.Serialization;

namespace SoulBuddy.Models;

public enum SessionLaunchMode
{
    Host,
    Join,
    Continue
}

public sealed class SoulLinkSession
{
    public required string Id { get; init; }

    // Compatibility alias for older UI code and previously stored sessions.
    // A session no longer has a separately configurable name; its ID is the
    // only identifier.
    [JsonIgnore]
    public string Name => Id;

    public DateTimeOffset CreatedAtUtc { get; init; } = DateTimeOffset.UtcNow;

    public DateTimeOffset UpdatedAtUtc { get; set; } = DateTimeOffset.UtcNow;

    public List<SessionPlayer> Players { get; init; } = [];
}

public sealed class SessionPlayer
{
    public required string Id { get; init; }

    public required string DisplayName { get; set; }

    public required int Slot { get; init; }

    public DateTimeOffset JoinedAtUtc { get; init; } = DateTimeOffset.UtcNow;
}

public sealed class ActiveSession
{
    public required string SessionId { get; init; }

    public required string PlayerId { get; init; }

    public SessionLaunchMode LaunchMode { get; init; } = SessionLaunchMode.Continue;
}

public sealed class SessionContext
{
    public required SoulLinkSession Session { get; init; }

    public required SessionPlayer LocalPlayer { get; init; }

    public SessionLaunchMode LaunchMode { get; init; } = SessionLaunchMode.Continue;
}
