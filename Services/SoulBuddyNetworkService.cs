using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;

namespace SoulBuddy.Services;

public enum SoulBuddyNetworkMode
{
    None,
    Host,
    Join
}

public enum SoulBuddyNetworkState
{
    Idle,
    Prepared,
    Waiting,
    Connecting,
    Connected,
    Error
}

public sealed class SoulBuddyNetworkService : IAsyncDisposable
{
    private const int TcpPort = 45831;
    private const int DiscoveryPort = 45832;
    private const int ProtocolVersion = 1;

    private readonly object _sync = new();
    private readonly SynchronizationContext? _synchronizationContext =
        SynchronizationContext.Current;

    private CancellationTokenSource? _cancellationSource;
    private TcpListener? _listener;
    private TcpClient? _client;
    private UdpClient? _discoveryClient;
    private Task? _networkTask;

    public SoulBuddyNetworkMode Mode { get; private set; } =
        SoulBuddyNetworkMode.None;

    public SoulBuddyNetworkState State { get; private set; } =
        SoulBuddyNetworkState.Idle;

    public string SessionId { get; private set; } = string.Empty;

    public string PlayerName { get; private set; } = string.Empty;

    public string RemotePlayerName { get; private set; } = string.Empty;

    public string StatusText { get; private set; } =
        "Netzwerk noch nicht gestartet.";

    public event EventHandler? StatusChanged;

    public void PrepareHost(string sessionId, string playerName)
    {
        Prepare(SoulBuddyNetworkMode.Host, sessionId, playerName);
        StartBackground(HostAsync);
    }

    public void PrepareJoin(string sessionId, string playerName)
    {
        Prepare(SoulBuddyNetworkMode.Join, sessionId, playerName);
        StartBackground(DiscoverAndJoinAsync);
    }

    public void Reset()
    {
        StopCurrentConnection();

        Mode = SoulBuddyNetworkMode.None;
        SessionId = string.Empty;
        PlayerName = string.Empty;
        RemotePlayerName = string.Empty;
        SetStatus(
            "Netzwerk noch nicht gestartet.",
            SoulBuddyNetworkState.Idle);
    }

    private void Prepare(
        SoulBuddyNetworkMode mode,
        string sessionId,
        string playerName)
    {
        if (string.IsNullOrWhiteSpace(sessionId))
        {
            throw new InvalidOperationException(
                "Für die Netzwerkverbindung wird eine Session-ID benötigt.");
        }

        if (string.IsNullOrWhiteSpace(playerName))
        {
            throw new InvalidOperationException(
                "Für die Netzwerkverbindung wird ein Spielername benötigt.");
        }

        StopCurrentConnection();

        Mode = mode;
        SessionId = sessionId.Trim();
        PlayerName = playerName.Trim();
        RemotePlayerName = string.Empty;
        State = SoulBuddyNetworkState.Prepared;
    }

    private void StartBackground(Func<CancellationToken, Task> action)
    {
        _cancellationSource = new CancellationTokenSource();
        var cancellationToken = _cancellationSource.Token;

        _networkTask = Task.Run(async () =>
        {
            try
            {
                await action(cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
            }
            catch (Exception ex)
            {
                SetStatus(
                    $"Netzwerkfehler: {ex.Message}",
                    SoulBuddyNetworkState.Error);
            }
        }, cancellationToken);
    }

    private async Task HostAsync(CancellationToken cancellationToken)
    {
        _listener = new TcpListener(IPAddress.Any, TcpPort);
        _listener.Start();

        var addresses = GetLocalIpv4Addresses();
        var addressText = addresses.Count == 0
            ? "lokale IP unbekannt"
            : string.Join(", ", addresses);

        SetStatus(
            $"Host aktiv · Warte auf Mitspieler · {addressText}",
            SoulBuddyNetworkState.Waiting);

        var discoveryTask = RunDiscoveryResponderAsync(cancellationToken);

        try
        {
            _client = await _listener.AcceptTcpClientAsync(cancellationToken);
        }
        finally
        {
            _listener.Stop();
            _listener = null;
            CloseDiscoveryClient();
        }

        try
        {
            await discoveryTask;
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }

        await CompleteHandshakeAsync(
            _client,
            isHost: true,
            cancellationToken);

        await KeepConnectionOpenAsync(_client, cancellationToken);
    }

    private async Task RunDiscoveryResponderAsync(
        CancellationToken cancellationToken)
    {
        _discoveryClient = new UdpClient(
            new IPEndPoint(IPAddress.Any, DiscoveryPort));

        while (!cancellationToken.IsCancellationRequested)
        {
            UdpReceiveResult received;

            try
            {
                received = await _discoveryClient.ReceiveAsync(
                    cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            DiscoveryMessage? request;

            try
            {
                request = JsonSerializer.Deserialize<DiscoveryMessage>(
                    received.Buffer);
            }
            catch (JsonException)
            {
                continue;
            }

            if (request is null ||
                request.Type != "discover" ||
                request.ProtocolVersion != ProtocolVersion ||
                !string.Equals(
                    request.SessionId,
                    SessionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var response = new DiscoveryMessage
            {
                Type = "host",
                ProtocolVersion = ProtocolVersion,
                SessionId = SessionId,
                PlayerName = PlayerName,
                TcpPort = TcpPort
            };

            var responseBytes = JsonSerializer.SerializeToUtf8Bytes(response);
            await _discoveryClient.SendAsync(
                responseBytes,
                received.RemoteEndPoint,
                cancellationToken);
        }
    }

    private async Task DiscoverAndJoinAsync(
        CancellationToken cancellationToken)
    {
        SetStatus(
            "Suche Host für diese Session im lokalen Netzwerk …",
            SoulBuddyNetworkState.Connecting);

        var host = await DiscoverHostAsync(cancellationToken);

        SetStatus(
            $"Host gefunden: {host.Address} · Verbinde …",
            SoulBuddyNetworkState.Connecting);

        _client = new TcpClient();
        await _client.ConnectAsync(
            host.Address,
            host.Port,
            cancellationToken);

        await CompleteHandshakeAsync(
            _client,
            isHost: false,
            cancellationToken);

        await KeepConnectionOpenAsync(_client, cancellationToken);
    }

    private async Task<DiscoveredHost> DiscoverHostAsync(
        CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(0)
        {
            EnableBroadcast = true
        };

        var request = new DiscoveryMessage
        {
            Type = "discover",
            ProtocolVersion = ProtocolVersion,
            SessionId = SessionId,
            PlayerName = PlayerName
        };
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request);

        await udp.SendAsync(
            requestBytes,
            new IPEndPoint(IPAddress.Broadcast, DiscoveryPort),
            cancellationToken);

        await udp.SendAsync(
            requestBytes,
            new IPEndPoint(IPAddress.Loopback, DiscoveryPort),
            cancellationToken);

        using var timeoutSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeoutSource.CancelAfter(TimeSpan.FromSeconds(8));

        while (true)
        {
            UdpReceiveResult received;

            try
            {
                received = await udp.ReceiveAsync(timeoutSource.Token);
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException(
                    "Kein Host für diese Session gefunden. " +
                    "Beide PCs müssen im selben Netzwerk sein und der Host muss zuerst gestartet werden.");
            }

            DiscoveryMessage? response;

            try
            {
                response = JsonSerializer.Deserialize<DiscoveryMessage>(
                    received.Buffer);
            }
            catch (JsonException)
            {
                continue;
            }

            if (response is null ||
                response.Type != "host" ||
                response.ProtocolVersion != ProtocolVersion ||
                response.TcpPort <= 0 ||
                !string.Equals(
                    response.SessionId,
                    SessionId,
                    StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            return new DiscoveredHost(
                received.RemoteEndPoint.Address,
                response.TcpPort);
        }
    }

    private async Task CompleteHandshakeAsync(
        TcpClient client,
        bool isHost,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();

        using var reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);

        using var writer = new StreamWriter(
            stream,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            bufferSize: 1024,
            leaveOpen: true)
        {
            AutoFlush = true
        };

        var localHello = new NetworkHello
        {
            ProtocolVersion = ProtocolVersion,
            SessionId = SessionId,
            PlayerName = PlayerName
        };

        NetworkHello remoteHello;

        if (isHost)
        {
            remoteHello = await ReadHelloAsync(reader, cancellationToken);
            await writer.WriteLineAsync(JsonSerializer.Serialize(localHello));
        }
        else
        {
            await writer.WriteLineAsync(JsonSerializer.Serialize(localHello));
            remoteHello = await ReadHelloAsync(reader, cancellationToken);
        }

        if (remoteHello.ProtocolVersion != ProtocolVersion)
        {
            throw new InvalidOperationException(
                "Der Mitspieler verwendet eine inkompatible Protokollversion.");
        }

        if (!string.Equals(
                remoteHello.SessionId,
                SessionId,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Session-ID stimmt nicht überein: {remoteHello.SessionId}");
        }

        if (string.IsNullOrWhiteSpace(remoteHello.PlayerName))
        {
            throw new InvalidOperationException(
                "Der Mitspieler hat keinen gültigen Spielernamen gesendet.");
        }

        RemotePlayerName = remoteHello.PlayerName.Trim();
        SetStatus(
            $"🟢 Verbunden mit {RemotePlayerName} · Session {SessionId}",
            SoulBuddyNetworkState.Connected);
    }

    private static async Task<NetworkHello> ReadHelloAsync(
        StreamReader reader,
        CancellationToken cancellationToken)
    {
        var line = await reader.ReadLineAsync(cancellationToken);

        if (string.IsNullOrWhiteSpace(line))
        {
            throw new InvalidOperationException(
                "Der Mitspieler hat keine Verbindungsdaten gesendet.");
        }

        return JsonSerializer.Deserialize<NetworkHello>(line) ??
               throw new InvalidOperationException(
                   "Die Verbindungsdaten des Mitspielers sind ungültig.");
    }

    private async Task KeepConnectionOpenAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        var buffer = new byte[1];
        var stream = client.GetStream();

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var bytesRead = await stream.ReadAsync(
                    buffer,
                    cancellationToken);

                if (bytesRead == 0)
                {
                    break;
                }
            }
        }
        finally
        {
            if (!cancellationToken.IsCancellationRequested)
            {
                SetStatus(
                    "Mitspieler hat die Verbindung getrennt.",
                    SoulBuddyNetworkState.Idle);
            }
        }
    }

    private static IReadOnlyList<string> GetLocalIpv4Addresses()
    {
        var addresses = new List<string>();

        foreach (var networkInterface in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (networkInterface.OperationalStatus != OperationalStatus.Up ||
                networkInterface.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var address in networkInterface
                         .GetIPProperties()
                         .UnicastAddresses)
            {
                if (address.Address.AddressFamily == AddressFamily.InterNetwork &&
                    !IPAddress.IsLoopback(address.Address))
                {
                    addresses.Add(address.Address.ToString());
                }
            }
        }

        return addresses.Distinct().ToArray();
    }

    private void SetStatus(
        string statusText,
        SoulBuddyNetworkState state)
    {
        lock (_sync)
        {
            StatusText = statusText;
            State = state;
        }

        RaiseStatusChanged();
    }

    private void RaiseStatusChanged()
    {
        if (_synchronizationContext is null)
        {
            StatusChanged?.Invoke(this, EventArgs.Empty);
            return;
        }

        _synchronizationContext.Post(
            _ => StatusChanged?.Invoke(this, EventArgs.Empty),
            null);
    }

    private void CloseDiscoveryClient()
    {
        _discoveryClient?.Close();
        _discoveryClient?.Dispose();
        _discoveryClient = null;
    }

    private void StopCurrentConnection()
    {
        try
        {
            _cancellationSource?.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }

        CloseDiscoveryClient();

        _listener?.Stop();
        _listener = null;

        _client?.Close();
        _client?.Dispose();
        _client = null;

        _cancellationSource?.Dispose();
        _cancellationSource = null;
        _networkTask = null;
    }

    public async ValueTask DisposeAsync()
    {
        var task = _networkTask;
        StopCurrentConnection();

        if (task is not null)
        {
            try
            {
                await task;
            }
            catch (OperationCanceledException)
            {
            }
        }
    }

    private sealed class NetworkHello
    {
        public int ProtocolVersion { get; init; }
        public string SessionId { get; init; } = string.Empty;
        public string PlayerName { get; init; } = string.Empty;
    }

    private sealed class DiscoveryMessage
    {
        public string Type { get; init; } = string.Empty;
        public int ProtocolVersion { get; init; }
        public string SessionId { get; init; } = string.Empty;
        public string PlayerName { get; init; } = string.Empty;
        public int TcpPort { get; init; }
    }

    private sealed record DiscoveredHost(IPAddress Address, int Port);
}
