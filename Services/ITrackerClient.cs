using SoulBuddy.Models;

namespace SoulBuddy.Services;

/// <summary>
/// Common contract for external Nuzlocke/SoulLink trackers.
/// SoulBuddy owns the local player's state after the initial import, while partner
/// state is refreshed read-only by SyncService.
/// </summary>
public interface ITrackerClient
{
    string? PartnerPlayerName { get; }

    bool IsSynchronizationHealthy { get; }

    Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken);

    Task<SoullockeRun?> LoadPartnerRunAsync(CancellationToken cancellationToken);

    Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken);
}

/// <summary>
/// Adapter that keeps the existing soullocke.com client untouched while exposing
/// it through the provider-neutral tracker contract.
/// </summary>
public sealed class LegacySoullockeTrackerClient : ITrackerClient
{
    private readonly SoullockeClient _inner;

    public LegacySoullockeTrackerClient(HttpClient httpClient, AppConfig config)
    {
        _inner = new SoullockeClient(httpClient, config);
    }

    public string? PartnerPlayerName => _inner.PartnerPlayerName;

    public bool IsSynchronizationHealthy => SoullockeClient.IsServerSynchronizationHealthy;

    public Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken) =>
        _inner.LoadRunAsync(cancellationToken);

    public Task<SoullockeRun?> LoadPartnerRunAsync(CancellationToken cancellationToken) =>
        _inner.LoadPartnerRunAsync(cancellationToken);

    public Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken) =>
        _inner.SaveRunAsync(encounters, cancellationToken);
}
