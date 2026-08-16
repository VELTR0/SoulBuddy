using SoulBuddy.Models;

namespace SoulBuddy.Services;

/// <summary>
/// Stable provider boundary for soullocke.vercel.app.
///
/// The deployed tracker belongs to the older Firebase project "soullocke-f7500".
/// Its production Realtime Database uses the legacy project-id endpoint
/// (soullocke-f7500.firebaseio.com), while VercelSoullockeClient also keeps the
/// newer *-default-rtdb endpoint shapes as fallbacks. Prefer an explicit user
/// override when one is configured.
/// </summary>
public sealed class VercelTrackerClient : ITrackerClient
{
    private const string DatabaseOverrideVariable = "SOULBUDDY_VERCEL_SOULLOCKE_DATABASE_URL";
    private const string ProductionDatabaseUrl = "https://soullocke-f7500.firebaseio.com";

    private readonly VercelSoullockeClient _inner;

    public VercelTrackerClient(HttpClient httpClient, AppConfig config)
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DatabaseOverrideVariable)))
            Environment.SetEnvironmentVariable(DatabaseOverrideVariable, ProductionDatabaseUrl);

        _inner = new VercelSoullockeClient(httpClient, config);
    }

    public string? PartnerPlayerName => _inner.PartnerPlayerName;
    public string SessionGameName => _inner.SessionGameName;
    public bool IsSynchronizationHealthy => _inner.IsSynchronizationHealthy;

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
