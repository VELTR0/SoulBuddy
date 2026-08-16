using SoulBuddy.Models;

namespace SoulBuddy.Services;

public static class TrackerClientFactory
{
    public static ITrackerClient Create(HttpClient httpClient, AppConfig config) =>
        config.TrackerProvider switch
        {
            TrackerProvider.SoullockeVercel => new VercelSoullockeClient(httpClient, config),
            _ => new SoullockeDotComTrackerClient(httpClient, config)
        };
}
