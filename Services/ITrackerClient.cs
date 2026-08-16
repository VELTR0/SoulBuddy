using SoulBuddy.Models;

namespace SoulBuddy.Services;

public interface ITrackerClient
{
    string? PartnerPlayerName { get; }
    string SessionGameName { get; }
    bool IsSynchronizationHealthy { get; }

    Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken);
    Task<SoullockeRun?> LoadPartnerRunAsync(CancellationToken cancellationToken);
    Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken);
    Task<bool> MarkLinkedPartnerBroFailedAsync(
        string location,
        CancellationToken cancellationToken);
}
