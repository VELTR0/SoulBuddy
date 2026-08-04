using SoulBuddy.Models;

namespace SoulBuddy.Services;

public static class SoullockeLaunchSettings
{
    private static readonly object Sync = new();

    public static bool Enabled { get; private set; }
    public static string Link { get; private set; } = string.Empty;
    public static string Password { get; private set; } = string.Empty;
    public static string PlayerName { get; private set; } = string.Empty;

    public static void Configure(
        bool enabled,
        string link,
        string password,
        string playerName)
    {
        lock (Sync)
        {
            Enabled = enabled;
            Link = enabled ? link.Trim() : string.Empty;
            Password = enabled ? password : string.Empty;
            PlayerName = playerName.Trim();
        }
    }

    public static AppConfig Apply(AppConfig config)
    {
        lock (Sync)
        {
            if (!Enabled)
            {
                return config;
            }

            var sessionId = ExtractSessionId(Link);
            return new AppConfig
            {
                PartyJsonPath = config.PartyJsonPath,
                SessionId = sessionId,
                PlayerId = config.PlayerId,
                TeamName = string.IsNullOrWhiteSpace(config.TeamName)
                    ? PlayerName
                    : config.TeamName,
                PlayerName = PlayerName,
                AuthToken = Password,
                RunNumber = config.RunNumber,
                PollIntervalMilliseconds = config.PollIntervalMilliseconds,
                DryRun = false,
                SoullockeEnabled = true
            };
        }
    }

    public static string ExtractSessionId(string link)
    {
        if (string.IsNullOrWhiteSpace(link))
        {
            throw new ArgumentException("Bitte gib den Soullocke-Link ein.", nameof(link));
        }

        var value = link.Trim();
        if (Uri.TryCreate(value, UriKind.Absolute, out var uri))
        {
            var query = uri.Query.TrimStart('?')
                .Split('&', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => part.Split('=', 2))
                .FirstOrDefault(parts =>
                    parts.Length == 2 &&
                    (string.Equals(parts[0], "sessionId", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(parts[0], "session", StringComparison.OrdinalIgnoreCase) ||
                     string.Equals(parts[0], "id", StringComparison.OrdinalIgnoreCase)));

            if (query is not null)
            {
                var decoded = Uri.UnescapeDataString(query[1]).Trim();
                if (!string.IsNullOrWhiteSpace(decoded))
                {
                    return decoded;
                }
            }

            var lastSegment = uri.Segments
                .Select(segment => segment.Trim('/'))
                .LastOrDefault(segment => !string.IsNullOrWhiteSpace(segment));
            if (!string.IsNullOrWhiteSpace(lastSegment))
            {
                return Uri.UnescapeDataString(lastSegment);
            }
        }

        // Also accept a raw Soullocke session ID for compatibility.
        if (!value.Contains(' ') && value.Length >= 3)
        {
            return value;
        }

        throw new ArgumentException(
            "Der Soullocke-Link enthält keine erkennbare Session-ID.",
            nameof(link));
    }
}
