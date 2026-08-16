using SoulBuddy.Models;

namespace SoulBuddy.Services;

internal sealed class SoullockeDotComTrackerClient : ITrackerClient
{
    private readonly SoullockeClient _inner;

    public SoullockeDotComTrackerClient(HttpClient httpClient, AppConfig config)
    {
        _inner = new SoullockeClient(httpClient, config);
    }

    public string? PartnerPlayerName => _inner.PartnerPlayerName;
    public string SessionGameName => _inner.SessionGameName;
    public bool IsSynchronizationHealthy => SoullockeClient.IsServerSynchronizationHealthy;

    public Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken) =>
        _inner.LoadRunAsync(cancellationToken);

    public Task<SoullockeRun?> LoadPartnerRunAsync(CancellationToken cancellationToken) =>
        _inner.LoadPartnerRunAsync(cancellationToken);

    public Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken) =>
        _inner.SaveRunAsync(encounters, cancellationToken);

    public Task<bool> MarkLinkedPartnerBroFailedAsync(
        string location,
        CancellationToken cancellationToken) =>
        _inner.MarkLinkedPartnerBroFailedAsync(location, cancellationToken);
}
