using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using SoulBuddy.Models;

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
    public const int DefaultTcpPort = 45831;
    private const int DiscoveryPort = 45832;
    private const int ProtocolVersion = 1;

    private readonly object _sync = new();
    private readonly SemaphoreSlim _sendLock = new(1, 1);
    private readonly SynchronizationContext? _synchronizationContext =
        SynchronizationContext.Current;
    private readonly UpnpPortMapper _portMapper = new();

    private CancellationTokenSource? _cancellationSource;
    private TcpListener? _listener;
    private TcpClient? _client;
    private UdpClient? _discoveryClient;
    private StreamReader? _reader;
    private StreamWriter? _writer;
    private Task? _networkTask;
    private bool _portMappingCreated;

    public SoulBuddyNetworkService()
    {
        Current = this;
    }

    public static SoulBuddyNetworkService? Current { get; private set; }

    public SoulBuddyNetworkMode Mode { get; private set; } =
        SoulBuddyNetworkMode.None;

    public SoulBuddyNetworkState State { get; private set; } =
        SoulBuddyNetworkState.Idle;

    public string SessionId { get; private set; } = string.Empty;
    public string PlayerName { get; private set; } = string.Empty;
    public string RemotePlayerName { get; private set; } = string.Empty;
    public string JoinAddress { get; set; } = string.Empty;
    public string InternetAddress { get; private set; } = string.Empty;
    public NetworkPlayerSnapshot? LatestRemoteSnapshot { get; private set; }

    public string StatusText { get; private set; } =
        "Netzwerk noch nicht gestartet.";

    public event EventHandler? StatusChanged;
    public event EventHandler<NetworkPlayerSnapshot>? PlayerSnapshotReceived;

    public void PrepareHost(string sessionId, string playerName)
    {
        Prepare(SoulBuddyNetworkMode.Host, sessionId, playerName);
        StartBackground(HostAsync);
    }

    public void PrepareJoin(string sessionId, string playerName)
    {
        PrepareJoin(sessionId, playerName, JoinAddress);
    }

    public void PrepareJoin(
        string sessionId,
        string playerName,
        string? internetAddress)
    {
        JoinAddress = internetAddress?.Trim() ?? string.Empty;
        Prepare(SoulBuddyNetworkMode.Join, sessionId, playerName);
        StartBackground(DiscoverAndJoinAsync);
    }

    public async Task SendPlayerSnapshotAsync(
        NetworkPlayerSnapshot snapshot,
        CancellationToken cancellationToken = default)
    {
        if (State != SoulBuddyNetworkState.Connected || _writer is null)
        {
            return;
        }

        var envelope = new NetworkEnvelope
        {
            Type = "player-snapshot",
            PlayerSnapshot = snapshot
        };

        await _sendLock.WaitAsync(cancellationToken);
        try
        {
            if (_writer is not null)
            {
                await _writer.WriteLineAsync(
                    JsonSerializer.Serialize(envelope));
            }
        }
        catch (IOException ex)
        {
            SetStatus(
                $"Synchronisierung fehlgeschlagen: {ex.Message}",
                SoulBuddyNetworkState.Error);
        }
        finally
        {
            _sendLock.Release();
        }
    }

    public void Reset()
    {
        StopCurrentConnection();
        Mode = SoulBuddyNetworkMode.None;
        SessionId = string.Empty;
        PlayerName = string.Empty;
        RemotePlayerName = string.Empty;
        InternetAddress = string.Empty;
        LatestRemoteSnapshot = null;
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
        LatestRemoteSnapshot = null;
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
        _listener = new TcpListener(IPAddress.Any, DefaultTcpPort);
        _listener.Start();

        var addresses = GetLocalIpv4Addresses();
        var localAddressText = addresses.Count == 0
            ? "lokale IP unbekannt"
            : string.Join(", ", addresses.Select(address =>
                $"{address}:{DefaultTcpPort}"));

        // LAN discovery must be available immediately. UPnP can take several
        // seconds and must never delay local players from finding the host.
        var discoveryTask = RunDiscoveryResponderAsync(cancellationToken);
        SetStatus(
            $"Host aktiv · Warte auf Mitspieler · Lokal: {localAddressText}",
            SoulBuddyNetworkState.Waiting);

        _ = TryPrepareInternetMappingAsync(localAddressText, cancellationToken);

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
        catch (Exception) when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (SocketException)
        {
        }

        await CompleteHandshakeAsync(_client, isHost: true, cancellationToken);
        await ReceiveMessagesAsync(cancellationToken);
    }

    private async Task TryPrepareInternetMappingAsync(
        string localAddressText,
        CancellationToken cancellationToken)
    {
        try
        {
            var mapping = await _portMapper.TryCreateMappingAsync(
                DefaultTcpPort,
                cancellationToken);
            _portMappingCreated = mapping.Success;
            InternetAddress = mapping.ExternalAddress ?? string.Empty;

            if (State == SoulBuddyNetworkState.Waiting &&
                mapping.Success &&
                !string.IsNullOrWhiteSpace(InternetAddress))
            {
                SetStatus(
                    $"Host aktiv · Warte auf Mitspieler · Lokal: {localAddressText} · Internet: {InternetAddress}",
                    SoulBuddyNetworkState.Waiting);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch
        {
            InternetAddress = string.Empty;
        }
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
                received = await _discoveryClient.ReceiveAsync(cancellationToken);
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (SocketException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }

            DiscoveryMessage? request;
            try
            {
                request = JsonSerializer.Deserialize<DiscoveryMessage>(received.Buffer);
            }
            catch (JsonException)
            {
                continue;
            }

            if (request is null ||
                request.Type != "discover" ||
                request.ProtocolVersion != ProtocolVersion ||
                !string.Equals(request.SessionId, SessionId, StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            var response = new DiscoveryMessage
            {
                Type = "host",
                ProtocolVersion = ProtocolVersion,
                SessionId = SessionId,
                PlayerName = PlayerName,
                TcpPort = DefaultTcpPort
            };

            await _discoveryClient.SendAsync(
                JsonSerializer.SerializeToUtf8Bytes(response),
                received.RemoteEndPoint,
                cancellationToken);
        }
    }

    private async Task DiscoverAndJoinAsync(CancellationToken cancellationToken)
    {
        SetStatus(
            string.IsNullOrWhiteSpace(JoinAddress)
                ? "Suche Host für diese Session automatisch im lokalen Netzwerk …"
                : "Suche lokal und versuche gleichzeitig die angegebene Internet-Adresse …",
            SoulBuddyNetworkState.Connecting);

        using var raceSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var candidates = new List<Task<TcpClient?>>
        {
            ConnectDiscoveredHostAsync(raceSource.Token)
        };

        if (!string.IsNullOrWhiteSpace(JoinAddress))
        {
            candidates.Add(ConnectDirectAsync(JoinAddress, raceSource.Token));
        }

        TcpClient? winner = null;
        var failures = new List<Exception>();

        while (candidates.Count > 0 && winner is null)
        {
            var completed = await Task.WhenAny(candidates);
            candidates.Remove(completed);
            try
            {
                winner = await completed;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                failures.Add(ex);
            }
        }

        if (winner is null)
        {
            var detail = failures.LastOrDefault()?.Message;
            throw new InvalidOperationException(
                string.IsNullOrWhiteSpace(detail)
                    ? "Kein Host gefunden. Prüfe die Session-ID."
                    : $"Kein Host gefunden: {detail}");
        }

        raceSource.Cancel();
        _client = winner;
        await CompleteHandshakeAsync(_client, isHost: false, cancellationToken);
        await ReceiveMessagesAsync(cancellationToken);
    }

    private async Task<TcpClient?> ConnectDiscoveredHostAsync(
        CancellationToken cancellationToken)
    {
        var host = await DiscoverHostAsync(cancellationToken);
        var client = new TcpClient();
        try
        {
            await client.ConnectAsync(host.Address, host.Port, cancellationToken);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static async Task<TcpClient?> ConnectDirectAsync(
        string address,
        CancellationToken cancellationToken)
    {
        var endpoint = ParseInternetAddress(address);
        var client = new TcpClient();
        try
        {
            using var timeoutSource =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeoutSource.CancelAfter(TimeSpan.FromSeconds(15));
            await client.ConnectAsync(
                endpoint.Host,
                endpoint.Port,
                timeoutSource.Token);
            return client;
        }
        catch
        {
            client.Dispose();
            throw;
        }
    }

    private static InternetEndpoint ParseInternetAddress(string value)
    {
        var trimmed = value.Trim();
        if (Uri.TryCreate(
                trimmed.Contains("://", StringComparison.Ordinal)
                    ? trimmed
                    : $"tcp://{trimmed}",
                UriKind.Absolute,
                out var uri) &&
            !string.IsNullOrWhiteSpace(uri.Host))
        {
            return new InternetEndpoint(
                uri.Host,
                uri.IsDefaultPort ? DefaultTcpPort : uri.Port);
        }

        throw new InvalidOperationException(
            "Die Internet-Adresse ist ungültig.");
    }

    private async Task<DiscoveredHost> DiscoverHostAsync(
        CancellationToken cancellationToken)
    {
        using var udp = new UdpClient(0) { EnableBroadcast = true };
        var request = new DiscoveryMessage
        {
            Type = "discover",
            ProtocolVersion = ProtocolVersion,
            SessionId = SessionId,
            PlayerName = PlayerName
        };
        var requestBytes = JsonSerializer.SerializeToUtf8Bytes(request);

        using var overallTimeout =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        overallTimeout.CancelAfter(TimeSpan.FromSeconds(20));

        while (!overallTimeout.IsCancellationRequested)
        {
            await udp.SendAsync(
                requestBytes,
                new IPEndPoint(IPAddress.Broadcast, DiscoveryPort),
                overallTimeout.Token);
            await udp.SendAsync(
                requestBytes,
                new IPEndPoint(IPAddress.Loopback, DiscoveryPort),
                overallTimeout.Token);

            using var receiveWindow =
                CancellationTokenSource.CreateLinkedTokenSource(overallTimeout.Token);
            receiveWindow.CancelAfter(TimeSpan.FromMilliseconds(900));

            try
            {
                while (true)
                {
                    var received = await udp.ReceiveAsync(receiveWindow.Token);
                    DiscoveryMessage? response;
                    try
                    {
                        response = JsonSerializer.Deserialize<DiscoveryMessage>(received.Buffer);
                    }
                    catch (JsonException)
                    {
                        continue;
                    }

                    if (response is null ||
                        response.Type != "host" ||
                        response.ProtocolVersion != ProtocolVersion ||
                        response.TcpPort <= 0 ||
                        !string.Equals(response.SessionId, SessionId, StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    return new DiscoveredHost(
                        received.RemoteEndPoint.Address,
                        response.TcpPort);
                }
            }
            catch (OperationCanceledException)
                when (!cancellationToken.IsCancellationRequested &&
                      !overallTimeout.IsCancellationRequested)
            {
                // No answer in this short window. Broadcast again.
            }
        }

        throw new InvalidOperationException(
            "Im lokalen Netzwerk wurde kein passender Host gefunden.");
    }

    private async Task CompleteHandshakeAsync(
        TcpClient client,
        bool isHost,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        _reader = new StreamReader(
            stream,
            Encoding.UTF8,
            detectEncodingFromByteOrderMarks: true,
            bufferSize: 1024,
            leaveOpen: true);
        _writer = new StreamWriter(
            stream,
            new UTF8Encoding(false),
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
            remoteHello = await ReadHelloAsync(_reader, cancellationToken);
            await _writer.WriteLineAsync(JsonSerializer.Serialize(localHello));
        }
        else
        {
            await _writer.WriteLineAsync(JsonSerializer.Serialize(localHello));
            remoteHello = await ReadHelloAsync(_reader, cancellationToken);
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

    private async Task ReceiveMessagesAsync(CancellationToken cancellationToken)
    {
        if (_reader is null)
        {
            return;
        }

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var line = await _reader.ReadLineAsync(cancellationToken);
                if (line is null)
                {
                    break;
                }

                NetworkEnvelope? envelope;
                try
                {
                    envelope = JsonSerializer.Deserialize<NetworkEnvelope>(line);
                }
                catch (JsonException)
                {
                    continue;
                }

                if (envelope?.Type == "player-snapshot" &&
                    envelope.PlayerSnapshot is not null)
                {
                    LatestRemoteSnapshot = envelope.PlayerSnapshot;
                    RaisePlayerSnapshotReceived(envelope.PlayerSnapshot);
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

            foreach (var address in networkInterface.GetIPProperties().UnicastAddresses)
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

    private void SetStatus(string statusText, SoulBuddyNetworkState state)
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
        PostToSynchronizationContext(() =>
            StatusChanged?.Invoke(this, EventArgs.Empty));
    }

    private void RaisePlayerSnapshotReceived(NetworkPlayerSnapshot snapshot)
    {
        PostToSynchronizationContext(() =>
            PlayerSnapshotReceived?.Invoke(this, snapshot));
    }

    private void PostToSynchronizationContext(Action action)
    {
        if (_synchronizationContext is null)
        {
            action();
            return;
        }

        _synchronizationContext.Post(_ => action(), null);
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
        _reader?.Dispose();
        _reader = null;
        _writer?.Dispose();
        _writer = null;
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

        if (_portMappingCreated)
        {
            try
            {
                await _portMapper.TryDeleteMappingAsync(DefaultTcpPort);
            }
            catch
            {
            }
            _portMappingCreated = false;
        }

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

        if (ReferenceEquals(Current, this))
        {
            Current = null;
        }
        _sendLock.Dispose();
        _portMapper.Dispose();
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

    private sealed class NetworkEnvelope
    {
        public string Type { get; init; } = string.Empty;
        public NetworkPlayerSnapshot? PlayerSnapshot { get; init; }
    }

    private sealed record DiscoveredHost(IPAddress Address, int Port);
    private sealed record InternetEndpoint(string Host, int Port);
}
