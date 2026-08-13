namespace SoulBuddy.Services;

internal sealed class HeadlessStreamCoordinator : IAsyncDisposable
{
    private readonly LocalStreamService _streamService = new();
    private readonly LanStreamDiscoveryService _lanDiscovery = new();
    private CancellationTokenSource? _discoveryCancellation;
    private Task? _discoveryTask;

    public async Task StartAsync(CancellationToken cancellationToken = default)
    {
        var localUrl = await _streamService.StartOutgoingAsync(cancellationToken);
        try
        {
            await _lanDiscovery.StartAdvertisingAsync(localUrl, cancellationToken);
            _discoveryCancellation = new CancellationTokenSource();
            _discoveryTask = DiscoverPartnerAsync(_discoveryCancellation.Token);
        }
        catch
        {
            await _lanDiscovery.StopAdvertisingAsync();
            await _streamService.StopOutgoingAsync();
            throw;
        }
    }

    private async Task DiscoverPartnerAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var partnerUrl = await _lanDiscovery.DiscoverAsync(cancellationToken);
                if (!string.IsNullOrWhiteSpace(partnerUrl))
                {
                    await _streamService.SetIncomingUrlAsync(partnerUrl);
                    return;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
            catch
            {
            }

            try
            {
                await Task.Delay(500, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    public async ValueTask DisposeAsync()
    {
        var cancellation = _discoveryCancellation;
        var discoveryTask = _discoveryTask;
        _discoveryCancellation = null;
        _discoveryTask = null;

        if (cancellation is not null)
        {
            cancellation.Cancel();
            if (discoveryTask is not null)
            {
                try
                {
                    await discoveryTask;
                }
                catch (OperationCanceledException)
                {
                }
            }
            cancellation.Dispose();
        }

        await _streamService.SetIncomingUrlAsync(null);
        await _lanDiscovery.StopAdvertisingAsync();
        await _streamService.StopOutgoingAsync();
        await _lanDiscovery.DisposeAsync();
        await _streamService.DisposeAsync();
    }
}
