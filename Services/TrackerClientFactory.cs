using SoulBuddy.Models;

namespace SoulBuddy.Services;

public static class TrackerClientFactory
{
    public static ITrackerClient Create(HttpClient httpClient, AppConfig config)
    {
        DiagnosticLog.StartSession($"tracker provider={config.TrackerProvider}");
        DiagnosticLog.Info(
            "TrackerFactory",
            $"Creating tracker client: provider={config.TrackerProvider}; " +
            $"enabled={config.SoullockeEnabled}; session='{config.SessionId}'; " +
            $"player='{config.PlayerName}'.");

        return config.TrackerProvider switch
        {
            TrackerProvider.SoullockeVercel => new VercelTrackerClient(httpClient, config),
            _ => new SoullockeDotComTrackerClient(httpClient, config)
        };
    }
}
