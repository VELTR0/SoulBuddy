using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;

namespace SoulBuddy.Services;

internal sealed class LanStreamDiscoveryService : IAsyncDisposable
{
    private const int DiscoveryPort = 45837;
    private const string DiscoveryPrefix = "SOULBUDDY_DISCOVER_V1";
    private const string OfferPrefix = "SOULBUDDY_STREAM_V1";
    private static readonly IPAddress DiscoveryGroup = IPAddress.Parse("239.255.83.66");
    private static readonly TimeSpan DiscoveryTimeout = TimeSpan.FromMilliseconds(1500);

    private readonly string _instanceId =
        LuaLaunchContext.SafeToken ?? $"{Environment.MachineName}-{Environment.ProcessId}-{Guid.NewGuid():N}";
    private readonly object _partnerGate = new();

    private TcpListener? _proxyListener;
    private UdpClient? _advertisingSocket;
    private CancellationTokenSource? _advertisingCancellation;
    private Task? _proxyTask;
    private Task? _discoveryResponderTask;
    private int _sourcePort;
    private int _proxyPort;

    private TcpListener? _partnerResolverListener;
    private CancellationTokenSource? _partnerResolverCancellation;
    private Task? _partnerResolverTask;
    private int _partnerResolverPort;
    private string? _lastPartnerUrl;
    private string? _lastPartnerInstanceId;
    private string _localActivityTitle = "LIVE-STATUS";
    private string _localActivityText = "Warte auf Live-Daten aus dem Emulator …";

    public string? LanUrl { get; private set; }
    public bool IsAdvertising => _proxyListener is not null;

    public void SetLocalActivity(string? title, string? text)
    {
        lock (_partnerGate)
        {
            _localActivityTitle = string.IsNullOrWhiteSpace(title) ? "LIVE-STATUS" : title;
            _localActivityText = string.IsNullOrWhiteSpace(text)
                ? "Warte auf Live-Daten aus dem Emulator …"
                : text;
        }
    }

    public async Task<(string Title, string Text)?> QueryPartnerActivityAsync(
        CancellationToken cancellationToken = default)
    {
        string? partnerInstanceId;
        lock (_partnerGate)
            partnerInstanceId = _lastPartnerInstanceId;

        if (string.IsNullOrWhiteSpace(partnerInstanceId))
            return null;

        var partner = await DiscoverRemoteAsync(
            cancellationToken,
            requiredInstanceId: partnerInstanceId);
        if (partner is null)
            return null;

        lock (_partnerGate)
        {
            _lastPartnerUrl = partner.Url;
            _lastPartnerInstanceId = partner.InstanceId;
        }

        return (partner.ActivityTitle, partner.ActivityText);
    }

    public async Task StartAdvertisingAsync(
        string localStreamUrl,
        CancellationToken cancellationToken = default)
    {
        if (IsAdvertising)
            return;

        if (!Uri.TryCreate(localStreamUrl, UriKind.Absolute, out var sourceUri) ||
            sourceUri.Scheme != Uri.UriSchemeHttp)
        {
            throw new InvalidOperationException("Lokale Stream-Adresse ist ungültig.");
        }

        _sourcePort = sourceUri.IsDefaultPort ? 80 : sourceUri.Port;

        var listener = new TcpListener(IPAddress.Any, 0);
        listener.Start();
        _proxyListener = listener;
        _proxyPort = ((IPEndPoint)listener.LocalEndpoint).Port;

        var preferredAddress = GetPreferredLanAddress();
        LanUrl = $"http://{preferredAddress}:{_proxyPort}/stream";

        try
        {
            var udp = CreateMulticastListener();
            _advertisingSocket = udp;
            _advertisingCancellation = new CancellationTokenSource();

            _proxyTask = RunProxyServerAsync(
                listener,
                _advertisingCancellation.Token);
            _discoveryResponderTask = RunDiscoveryResponderAsync(
                udp,
                _advertisingCancellation.Token);

            await Task.CompletedTask;
        }
        catch
        {
            await StopAdvertisingAsync();
            throw;
        }
    }

    public async Task StopAdvertisingAsync()
    {
        await StopPartnerResolverAsync();

        var cancellation = _advertisingCancellation;
        var proxyTask = _proxyTask;
        var discoveryTask = _discoveryResponderTask;
        var listener = _proxyListener;
        var udp = _advertisingSocket;

        _advertisingCancellation = null;
        _proxyTask = null;
        _discoveryResponderTask = null;
        _proxyListener = null;
        _advertisingSocket = null;
        LanUrl = null;
        _sourcePort = 0;
        _proxyPort = 0;

        cancellation?.Cancel();
        listener?.Stop();
        udp?.Dispose();

        await IgnoreShutdownAsync(proxyTask);
        await IgnoreShutdownAsync(discoveryTask);
        cancellation?.Dispose();
    }

    public async Task<string?> DiscoverAsync(
        CancellationToken cancellationToken = default)
    {
        var remote = await DiscoverRemoteAsync(cancellationToken);
        if (remote is null)
            return null;

        lock (_partnerGate)
        {
            _lastPartnerUrl = remote.Url;
            _lastPartnerInstanceId = remote.InstanceId;
        }

        EnsurePartnerResolverStarted();
        return $"http://127.0.0.1:{_partnerResolverPort}/stream";
    }

    private async Task<DiscoveredPartner?> DiscoverRemoteAsync(
        CancellationToken cancellationToken,
        string? requiredInstanceId = null)
    {
        using var client = new UdpClient(AddressFamily.InterNetwork);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, 0));
        client.MulticastLoopback = true;
        client.Ttl = 1;

        var requestId = Guid.NewGuid().ToString("N");
        var payload = Encoding.UTF8.GetBytes(
            $"{DiscoveryPrefix}|{requestId}|{_instanceId}");
        var target = new IPEndPoint(DiscoveryGroup, DiscoveryPort);

        await client.SendAsync(payload, payload.Length, target);

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken);
        timeout.CancelAfter(DiscoveryTimeout);

        while (!timeout.IsCancellationRequested)
        {
            UdpReceiveResult response;
            try
            {
                response = await client.ReceiveAsync(timeout.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                return null;
            }

            var text = Encoding.UTF8.GetString(response.Buffer);
            var parts = text.Split('|');
            if (parts.Length < 4 ||
                !string.Equals(parts[0], OfferPrefix, StringComparison.Ordinal) ||
                !string.Equals(parts[1], requestId, StringComparison.Ordinal) ||
                string.Equals(parts[2], _instanceId, StringComparison.Ordinal) ||
                (!string.IsNullOrWhiteSpace(requiredInstanceId) &&
                 !string.Equals(parts[2], requiredInstanceId, StringComparison.Ordinal)) ||
                !int.TryParse(parts[3], out var port) ||
                port is <= 0 or > 65535)
            {
                continue;
            }

            var address = response.RemoteEndPoint.Address;
            if (address.AddressFamily != AddressFamily.InterNetwork)
                continue;

            return new DiscoveredPartner(
                $"http://{address}:{port}/stream",
                parts[2],
                parts.Length >= 5 ? DecodeActivity(parts[4]) : string.Empty,
                parts.Length >= 6 ? DecodeActivity(parts[5]) : string.Empty);
        }

        return null;
    }

    private void EnsurePartnerResolverStarted()
    {
        if (_partnerResolverListener is not null)
            return;

        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        _partnerResolverListener = listener;
        _partnerResolverPort = ((IPEndPoint)listener.LocalEndpoint).Port;
        _partnerResolverCancellation = new CancellationTokenSource();
        _partnerResolverTask = RunPartnerResolverAsync(
            listener,
            _partnerResolverCancellation.Token);
    }

    private async Task RunPartnerResolverAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = ResolvePartnerClientSafelyAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                await DelayBrieflyAsync(cancellationToken);
            }
        }
    }

    private async Task ResolvePartnerClientSafelyAsync(
        TcpClient localViewer,
        CancellationToken cancellationToken)
    {
        using (localViewer)
        {
            try
            {
                localViewer.NoDelay = true;

                var remoteUrl = GetLastPartnerUrl();
                if (!await TryProxyToPartnerAsync(
                        localViewer,
                        remoteUrl,
                        cancellationToken))
                {
                    ClearLastPartner(remoteUrl);
                    var remote = await DiscoverRemoteAsync(cancellationToken);
                    if (remote is null)
                        return;

                    lock (_partnerGate)
                    {
                        _lastPartnerUrl = remote.Url;
                        _lastPartnerInstanceId = remote.InstanceId;
                    }

                    if (!await TryProxyToPartnerAsync(
                            localViewer,
                            remote.Url,
                            cancellationToken))
                    {
                        ClearLastPartner(remote.Url);
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
            catch (ObjectDisposedException)
            {
            }
        }
    }

    private async Task<bool> TryProxyToPartnerAsync(
        TcpClient localViewer,
        string? remoteUrl,
        CancellationToken cancellationToken)
    {
        if (!Uri.TryCreate(remoteUrl, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp)
        {
            return false;
        }

        var port = uri.IsDefaultPort ? 80 : uri.Port;
        using var remoteClient = new TcpClient(AddressFamily.InterNetwork);
        try
        {
            remoteClient.NoDelay = true;
            await remoteClient.ConnectAsync(uri.Host, port, cancellationToken);

            await using var localStream = localViewer.GetStream();
            await using var remoteStream = remoteClient.GetStream();
            using var connectionCancellation =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            var toRemote = localStream.CopyToAsync(
                remoteStream,
                64 * 1024,
                connectionCancellation.Token);
            var toLocal = remoteStream.CopyToAsync(
                localStream,
                64 * 1024,
                connectionCancellation.Token);

            await Task.WhenAny(toRemote, toLocal);
            connectionCancellation.Cancel();

            try
            {
                await Task.WhenAll(toRemote, toLocal);
            }
            catch (OperationCanceledException)
            {
            }

            return true;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (IOException)
        {
            return false;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private string? GetLastPartnerUrl()
    {
        lock (_partnerGate)
            return _lastPartnerUrl;
    }

    private void ClearLastPartner(string? expectedUrl)
    {
        lock (_partnerGate)
        {
            if (!string.Equals(_lastPartnerUrl, expectedUrl, StringComparison.Ordinal))
                return;

            _lastPartnerUrl = null;
            _lastPartnerInstanceId = null;
        }
    }

    private async Task StopPartnerResolverAsync()
    {
        var cancellation = _partnerResolverCancellation;
        var task = _partnerResolverTask;
        var listener = _partnerResolverListener;

        _partnerResolverCancellation = null;
        _partnerResolverTask = null;
        _partnerResolverListener = null;
        _partnerResolverPort = 0;
        lock (_partnerGate)
        {
            _lastPartnerUrl = null;
            _lastPartnerInstanceId = null;
        }

        cancellation?.Cancel();
        listener?.Stop();
        await IgnoreShutdownAsync(task);
        cancellation?.Dispose();
    }

    private UdpClient CreateMulticastListener()
    {
        var client = new UdpClient(AddressFamily.InterNetwork);
        try
        {
            client.Client.ExclusiveAddressUse = false;
        }
        catch (PlatformNotSupportedException)
        {
        }

        client.Client.SetSocketOption(
            SocketOptionLevel.Socket,
            SocketOptionName.ReuseAddress,
            true);
        client.Client.Bind(new IPEndPoint(IPAddress.Any, DiscoveryPort));
        client.JoinMulticastGroup(DiscoveryGroup);
        client.MulticastLoopback = true;
        client.Ttl = 1;
        return client;
    }

    private async Task RunDiscoveryResponderAsync(
        UdpClient client,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var request = await client.ReceiveAsync(cancellationToken);
                var text = Encoding.UTF8.GetString(request.Buffer);
                var parts = text.Split('|');

                if (parts.Length != 3 ||
                    !string.Equals(parts[0], DiscoveryPrefix, StringComparison.Ordinal) ||
                    string.Equals(parts[2], _instanceId, StringComparison.Ordinal) ||
                    _proxyPort <= 0)
                {
                    continue;
                }

                string activityTitle;
                string activityText;
                lock (_partnerGate)
                {
                    activityTitle = _localActivityTitle;
                    activityText = _localActivityText;
                }

                var response = Encoding.UTF8.GetBytes(
                    $"{OfferPrefix}|{parts[1]}|{_instanceId}|{_proxyPort}|" +
                    $"{EncodeActivity(activityTitle)}|{EncodeActivity(activityText)}");
                await client.SendAsync(
                    response,
                    response.Length,
                    request.RemoteEndPoint);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                await DelayBrieflyAsync(cancellationToken);
            }
        }
    }

    private async Task RunProxyServerAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var client = await listener.AcceptTcpClientAsync(cancellationToken);
                _ = ProxyClientSafelyAsync(client, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                await DelayBrieflyAsync(cancellationToken);
            }
        }
    }

    private async Task ProxyClientSafelyAsync(
        TcpClient lanClient,
        CancellationToken cancellationToken)
    {
        using (lanClient)
        using (var localClient = new TcpClient(AddressFamily.InterNetwork))
        {
            try
            {
                lanClient.NoDelay = true;
                localClient.NoDelay = true;
                await localClient.ConnectAsync(
                    IPAddress.Loopback,
                    _sourcePort,
                    cancellationToken);

                await using var lanStream = lanClient.GetStream();
                await using var localStream = localClient.GetStream();
                using var connectionCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

                var toLocal = lanStream.CopyToAsync(
                    localStream,
                    64 * 1024,
                    connectionCancellation.Token);
                var toLan = localStream.CopyToAsync(
                    lanStream,
                    64 * 1024,
                    connectionCancellation.Token);

                await Task.WhenAny(toLocal, toLan);
                connectionCancellation.Cancel();

                try
                {
                    await Task.WhenAll(toLocal, toLan);
                }
                catch (OperationCanceledException)
                {
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (IOException)
            {
            }
            catch (SocketException)
            {
            }
        }
    }

    private static string EncodeActivity(string value) =>
        Convert.ToBase64String(Encoding.UTF8.GetBytes(value));

    private static string DecodeActivity(string value)
    {
        try
        {
            return Encoding.UTF8.GetString(Convert.FromBase64String(value));
        }
        catch (FormatException)
        {
            return string.Empty;
        }
    }

    private static string GetPreferredLanAddress()
    {
        try
        {
            var candidates = NetworkInterface.GetAllNetworkInterfaces()
                .Where(network =>
                    network.OperationalStatus == OperationalStatus.Up &&
                    network.NetworkInterfaceType != NetworkInterfaceType.Loopback)
                .SelectMany(network => network.GetIPProperties().UnicastAddresses)
                .Select(info => info.Address)
                .Where(address =>
                    address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address) &&
                    !IsLinkLocal(address))
                .ToArray();

            var privateAddress = candidates.FirstOrDefault(IsPrivateIpv4);
            return (privateAddress ?? candidates.FirstOrDefault() ?? IPAddress.Loopback)
                .ToString();
        }
        catch (NetworkInformationException)
        {
            return IPAddress.Loopback.ToString();
        }
    }

    private static bool IsPrivateIpv4(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 10 ||
               (bytes[0] == 172 && bytes[1] is >= 16 and <= 31) ||
               (bytes[0] == 192 && bytes[1] == 168);
    }

    private static bool IsLinkLocal(IPAddress address)
    {
        var bytes = address.GetAddressBytes();
        return bytes[0] == 169 && bytes[1] == 254;
    }

    private static async Task DelayBrieflyAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(100, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    private static async Task IgnoreShutdownAsync(Task? task)
    {
        if (task is null)
            return;

        try
        {
            await task;
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        await StopAdvertisingAsync();
    }

    private sealed record DiscoveredPartner(
        string Url,
        string InstanceId,
        string ActivityTitle,
        string ActivityText);
}
