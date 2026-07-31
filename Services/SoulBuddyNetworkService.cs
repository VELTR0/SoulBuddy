using System.Net;
using System.Net.Sockets;
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
    private const int Port = 45831;

    private readonly object _sync = new();
    private CancellationTokenSource? _cancellationSource;
    private TcpListener? _listener;
    private TcpClient? _client;
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
        StartBackground(JoinLocalhostAsync);
    }

    public void Reset()
    {
        StopCurrentConnection();

        Mode = SoulBuddyNetworkMode.None;
        State = SoulBuddyNetworkState.Idle;
        SessionId = string.Empty;
        PlayerName = string.Empty;
        RemotePlayerName = string.Empty;
        SetStatus("Netzwerk noch nicht gestartet.", SoulBuddyNetworkState.Idle);
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

    private void StartBackground(
        Func<CancellationToken, Task> action)
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
        _listener = new TcpListener(IPAddress.Any, Port);
        _listener.Start();

        SetStatus(
            $"Host aktiv · Warte auf Mitspieler · Port {Port}",
            SoulBuddyNetworkState.Waiting);

        _client = await _listener.AcceptTcpClientAsync(cancellationToken);
        _listener.Stop();
        _listener = null;

        await CompleteHandshakeAsync(_client, isHost: true, cancellationToken);
        await KeepConnectionOpenAsync(_client, cancellationToken);
    }

    private async Task JoinLocalhostAsync(CancellationToken cancellationToken)
    {
        SetStatus(
            $"Verbinde mit Host auf diesem PC · Port {Port} …",
            SoulBuddyNetworkState.Connecting);

        _client = new TcpClient();
        await _client.ConnectAsync(
            IPAddress.Loopback,
            Port,
            cancellationToken);

        await CompleteHandshakeAsync(_client, isHost: false, cancellationToken);
        await KeepConnectionOpenAsync(_client, cancellationToken);
    }

    private async Task CompleteHandshakeAsync(
        TcpClient client,
        bool isHost,
        CancellationToken cancellationToken)
    {
        var stream = client.GetStream();
        using var reader = new StreamReader(
            stream,
            leaveOpen: true);
        using var writer = new StreamWriter(
            stream,
            leaveOpen: true)
        {
            AutoFlush = true
        };

        var localHello = new NetworkHello
        {
            ProtocolVersion = 1,
            SessionId = SessionId,
            PlayerName = PlayerName
        };

        NetworkHello remoteHello;

        if (isHost)
        {
            remoteHello = await ReadHelloAsync(reader, cancellationToken);
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(localHello));
        }
        else
        {
            await writer.WriteLineAsync(
                JsonSerializer.Serialize(localHello));
            remoteHello = await ReadHelloAsync(reader, cancellationToken);
        }

        if (remoteHello.ProtocolVersion != 1)
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

    private void SetStatus(
        string statusText,
        SoulBuddyNetworkState state)
    {
        lock (_sync)
        {
            StatusText = statusText;
            State = state;
        }

        StatusChanged?.Invoke(this, EventArgs.Empty);
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
}
