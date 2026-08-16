using SoulBuddy.Models;

namespace SoulBuddy.Services;

public static class SoullockeLaunchSettings
{
    private static readonly object Sync = new();

    public static string Link { get; private set; } = string.Empty;
    public static string Password { get; private set; } = string.Empty;
    public static string PlayerName { get; private set; } = string.Empty;

    public static void Configure(
        string link,
        string password,
        string playerName)
    {
        lock (Sync)
        {
            Link = link.Trim();
            Password = password;
            PlayerName = playerName.Trim();
        }
    }

    public static AppConfig Apply(AppConfig config)
    {
        lock (Sync)
        {
            var tracker = TrackerLinkParser.Parse(Link);
            return new AppConfig
            {
                PartyJsonPath = config.PartyJsonPath,
                SessionId = tracker.RunId,
                PlayerId = config.PlayerId,
                TeamName = string.IsNullOrWhiteSpace(config.TeamName)
                    ? PlayerName
                    : config.TeamName,
                PlayerName = PlayerName,
                AuthToken = tracker.RequiresPassword ? Password : string.Empty,
                TrackerProvider = tracker.Provider,
                RunNumber = config.RunNumber,
                PollIntervalMilliseconds = config.PollIntervalMilliseconds,
                DryRun = false,
                SoullockeEnabled = true
            };
        }
    }

    // Kept for existing callers and stored profiles. New code should use
    // TrackerLinkParser when provider information is needed as well.
    public static string ExtractSessionId(string link) =>
        TrackerLinkParser.Parse(link).RunId;
}
