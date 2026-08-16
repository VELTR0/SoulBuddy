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
    private static readonly TimeSpan DiagnosticProbeTimeout = TimeSpan.FromSeconds(5);

    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly VercelSoullockeClient _inner;
    private readonly SemaphoreSlim _diagnosticsLock = new(1, 1);
    private bool _diagnosticsCompleted;

    public VercelTrackerClient(HttpClient httpClient, AppConfig config)
    {
        _httpClient = httpClient;
        _config = config;

        var configuredEndpoint = Environment.GetEnvironmentVariable(DatabaseOverrideVariable);
        if (string.IsNullOrWhiteSpace(configuredEndpoint))
        {
            configuredEndpoint = ProductionDatabaseUrl;
            Environment.SetEnvironmentVariable(DatabaseOverrideVariable, configuredEndpoint);
            DiagnosticLog.Info(
                "VercelTracker",
                $"No database override configured. Using built-in endpoint '{configuredEndpoint}'.");
        }
        else
        {
            DiagnosticLog.Info(
                "VercelTracker",
                $"Using database endpoint from {DatabaseOverrideVariable}: '{configuredEndpoint.TrimEnd('/')}'.");
        }

        _inner = new VercelSoullockeClient(httpClient, config);
    }

    public string? PartnerPlayerName => _inner.PartnerPlayerName;
    public string SessionGameName => _inner.SessionGameName;
    public bool IsSynchronizationHealthy => _inner.IsSynchronizationHealthy;

    public async Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken)
    {
        await EnsureDiagnosticsAsync(cancellationToken);
        return await ExecuteLoggedAsync(
            "LoadRunAsync",
            () => _inner.LoadRunAsync(cancellationToken));
    }

    public async Task<SoullockeRun?> LoadPartnerRunAsync(CancellationToken cancellationToken)
    {
        await EnsureDiagnosticsAsync(cancellationToken);
        return await ExecuteLoggedAsync(
            "LoadPartnerRunAsync",
            () => _inner.LoadPartnerRunAsync(cancellationToken));
    }

    public async Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken)
    {
        await EnsureDiagnosticsAsync(cancellationToken);
        DiagnosticLog.Info(
            "VercelTracker",
            $"SaveRunAsync starting with {encounters.Count} encounter(s). " +
            $"Current health={_inner.IsSynchronizationHealthy}.");

        try
        {
            await _inner.SaveRunAsync(encounters, cancellationToken);
            DiagnosticLog.Info(
                "VercelTracker",
                $"SaveRunAsync succeeded. Current health={_inner.IsSynchronizationHealthy}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Warning("VercelTracker", "SaveRunAsync cancelled by SoulBuddy shutdown.");
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception(
                "VercelTracker",
                $"SaveRunAsync failed. Current health={_inner.IsSynchronizationHealthy}",
                ex);
            throw;
        }
    }

    public async Task<bool> MarkLinkedPartnerBroFailedAsync(
        string location,
        CancellationToken cancellationToken)
    {
        await EnsureDiagnosticsAsync(cancellationToken);
        return await ExecuteLoggedAsync(
            "MarkLinkedPartnerBroFailedAsync",
            () => _inner.MarkLinkedPartnerBroFailedAsync(location, cancellationToken));
    }

    private async Task<T> ExecuteLoggedAsync<T>(string operation, Func<Task<T>> action)
    {
        DiagnosticLog.Info(
            "VercelTracker",
            $"{operation} starting. Current health={_inner.IsSynchronizationHealthy}.");

        try
        {
            var result = await action();
            DiagnosticLog.Info(
                "VercelTracker",
                $"{operation} succeeded. Current health={_inner.IsSynchronizationHealthy}; " +
                $"partner='{_inner.PartnerPlayerName ?? "<none>"}'; game='{_inner.SessionGameName}'.");
            return result;
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Warning(
                "VercelTracker",
                $"{operation} cancelled. Current health={_inner.IsSynchronizationHealthy}.");
            throw;
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception(
                "VercelTracker",
                $"{operation} failed. Current health={_inner.IsSynchronizationHealthy}",
                ex);
            throw;
        }
    }

    private async Task EnsureDiagnosticsAsync(CancellationToken cancellationToken)
    {
        if (_diagnosticsCompleted)
            return;

        await _diagnosticsLock.WaitAsync(cancellationToken);
        try
        {
            if (_diagnosticsCompleted)
                return;

            DiagnosticLog.Info(
                "VercelDiagnostics",
                $"Starting endpoint diagnostics for session='{_config.SessionId}', " +
                $"player='{_config.PlayerName}'.");

            var candidates = DatabaseCandidates().Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            DiagnosticLog.Info(
                "VercelDiagnostics",
                $"Firebase candidates ({candidates.Length}): {string.Join(" | ", candidates)}");

            var probes = candidates
                .Select(candidate => ProbeFirebaseCandidateAsync(candidate, cancellationToken))
                .Append(ProbeTrackerWebsiteAsync(cancellationToken));

            await Task.WhenAll(probes);
            DiagnosticLog.Info("VercelDiagnostics", "Endpoint diagnostics finished.");
            _diagnosticsCompleted = true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            DiagnosticLog.Warning("VercelDiagnostics", "Endpoint diagnostics cancelled by caller.");
            throw;
        }
        catch (Exception ex)
        {
            // Diagnostics are best-effort and must not block the real tracker client.
            DiagnosticLog.Exception("VercelDiagnostics", "Unexpected diagnostics failure", ex);
            _diagnosticsCompleted = true;
        }
        finally
        {
            _diagnosticsLock.Release();
        }
    }

    private async Task ProbeFirebaseCandidateAsync(
        string baseUrl,
        CancellationToken cancellationToken)
    {
        var session = Uri.EscapeDataString(_config.SessionId ?? string.Empty);
        var probeUrl = $"{baseUrl.TrimEnd('/')}/{session}.json?shallow=true";
        await ProbeUrlAsync("Firebase", baseUrl, probeUrl, cancellationToken);
    }

    private async Task ProbeTrackerWebsiteAsync(CancellationToken cancellationToken)
    {
        var session = Uri.EscapeDataString(_config.SessionId ?? string.Empty);
        var probeUrl = $"https://soullocke.vercel.app/run/{session}";
        await ProbeUrlAsync(
            "Website",
            "https://soullocke.vercel.app",
            probeUrl,
            cancellationToken);
    }

    private async Task ProbeUrlAsync(
        string kind,
        string displayTarget,
        string probeUrl,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(DiagnosticProbeTimeout);

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, probeUrl);
            using var response = await _httpClient.SendAsync(request, timeout.Token);
            var body = await response.Content.ReadAsStringAsync(timeout.Token);
            var bodySummary = SummarizeProbeBody(body, response.IsSuccessStatusCode);

            DiagnosticLog.Info(
                "VercelDiagnostics",
                $"{kind} probe '{displayTarget}' => HTTP {(int)response.StatusCode} " +
                $"{response.ReasonPhrase}; {bodySummary}.");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            DiagnosticLog.Warning(
                "VercelDiagnostics",
                $"{kind} probe '{displayTarget}' timed out after " +
                $"{DiagnosticProbeTimeout.TotalSeconds:0}s.");
        }
        catch (Exception ex)
        {
            DiagnosticLog.Exception(
                "VercelDiagnostics",
                $"{kind} probe '{displayTarget}' failed",
                ex);
        }
    }

    private static string SummarizeProbeBody(string body, bool success)
    {
        var trimmed = body.Trim();
        if (string.Equals(trimmed, "null", StringComparison.OrdinalIgnoreCase))
            return "body=null (run not present at this endpoint)";

        if (trimmed.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return "body reports Permission denied";

        if (success)
            return $"body is non-null ({body.Length} chars)";

        return $"error body received ({body.Length} chars; content omitted)";
    }

    private static IEnumerable<string> DatabaseCandidates()
    {
        var configured = Environment.GetEnvironmentVariable(DatabaseOverrideVariable);
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured.TrimEnd('/');

        yield return "https://soullocke-f7500-default-rtdb.firebaseio.com";
        yield return "https://soullocke-f7500-default-rtdb.europe-west1.firebasedatabase.app";
        yield return "https://soullocke-f7500-default-rtdb.asia-southeast1.firebasedatabase.app";
        yield return "https://soullocke-f7500-default-rtdb.firebasedatabase.app";
    }
}
