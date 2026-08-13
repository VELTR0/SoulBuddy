using System.Net;
using System.Net.Sockets;
using System.Text;

namespace SoulBuddy.Services;

internal sealed class LocalStreamService : IAsyncDisposable
{
    private static readonly TimeSpan CapturePollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan SendPollInterval = TimeSpan.FromMilliseconds(50);
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromMilliseconds(500);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan StaleFrameTimeout = TimeSpan.FromSeconds(3);

    private const int MaximumFrameBytes = 2 * 1024 * 1024;
    private const int MaximumHttpHeaderBytes = 16 * 1024;
    private const int OverlayWidth = 64;
    private const int OverlayHeight = 48;

    private readonly SemaphoreSlim _outgoingGate = new(1, 1);
    private readonly object _latestFrameGate = new();
    private readonly string _captureEnabledPath;
    private readonly string _outgoingFramePath;
    private readonly string _outgoingSequencePath;
    private readonly string _incomingFramePath;

    private TcpListener? _listener;
    private CancellationTokenSource? _outgoingCancellation;
    private Task? _outgoingServerTask;
    private Task? _captureBridgeTask;
    private CancellationTokenSource? _incomingCancellation;
    private Task? _incomingTask;

    private byte[]? _latestOutgoingFrame;
    private long _latestOutgoingVersion;
    private DateTimeOffset _lastCaptureAt = DateTimeOffset.MinValue;
    private string _incomingStatus = "Kein Stream verbunden";
    private string _outgoingStatus = "Stream nicht gestartet";

    public LocalStreamService()
    {
        var runtimeDirectory = FindRuntimeDirectory();
        Directory.CreateDirectory(runtimeDirectory);

        _captureEnabledPath = LuaLaunchContext.ScopePath(
            Path.Combine(runtimeDirectory, "stream-capture.enabled"));
        _outgoingFramePath = LuaLaunchContext.ScopePath(
            Path.Combine(runtimeDirectory, "stream-out.gd"));
        _outgoingSequencePath = LuaLaunchContext.ScopePath(
            Path.Combine(runtimeDirectory, "stream-out.seq"));
        _incomingFramePath = LuaLaunchContext.ScopePath(
            Path.Combine(runtimeDirectory, "stream-in.gd"));
    }

    public event EventHandler? StatusChanged;
    public event Action<byte[]?>? OutgoingFrameChanged;
    public event Action<byte[]?>? IncomingFrameChanged;

    public string? OutgoingUrl { get; private set; }
    public bool IsOutgoingRunning => _listener is not null;
    public string IncomingStatus => _incomingStatus;
    public string OutgoingStatus => _outgoingStatus;

    public async Task<string> StartOutgoingAsync(CancellationToken cancellationToken = default)
    {
        await _outgoingGate.WaitAsync(cancellationToken);
        try
        {
            if (_listener is not null && !string.IsNullOrWhiteSpace(OutgoingUrl))
                return OutgoingUrl;

            TryDeleteFile(_outgoingFramePath);
            TryDeleteFile(_outgoingSequencePath);

            lock (_latestFrameGate)
            {
                _latestOutgoingFrame = null;
                _latestOutgoingVersion = 0;
                _lastCaptureAt = DateTimeOffset.MinValue;
            }

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            OutgoingUrl = $"http://127.0.0.1:{endpoint.Port}/stream";
            _listener = listener;
            _outgoingCancellation = new CancellationTokenSource();

            await File.WriteAllTextAsync(_captureEnabledPath, "1", cancellationToken);

            SetOutgoingStatus("Aufnahme gestartet · warte auf DeSmuME-Frames …");
            _captureBridgeTask = RunCaptureBridgeAsync(_outgoingCancellation.Token);
            _outgoingServerTask = RunOutgoingServerAsync(
                listener,
                _outgoingCancellation.Token);

            return OutgoingUrl;
        }
        catch
        {
            await StopOutgoingCoreAsync();
            throw;
        }
        finally
        {
            _outgoingGate.Release();
        }
    }

    public async Task StopOutgoingAsync()
    {
        await _outgoingGate.WaitAsync();
        try
        {
            await StopOutgoingCoreAsync();
        }
        finally
        {
            _outgoingGate.Release();
        }
    }

    public async Task SetIncomingUrlAsync(string? value)
    {
        await StopIncomingAsync(deleteFrame: true);

        var text = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
        {
            SetIncomingStatus("Kein Stream verbunden");
            IncomingFrameChanged?.Invoke(null);
            return;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp)
        {
            SetIncomingStatus("Ungültige Stream-Adresse");
            IncomingFrameChanged?.Invoke(null);
            return;
        }

        if (string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/")
            uri = new Uri(uri, "/stream");

        _incomingCancellation = new CancellationTokenSource();
        SetIncomingStatus("Verbindung zum Partner-Stream wird hergestellt …");
        _incomingTask = RunIncomingClientAsync(uri, _incomingCancellation.Token);
    }

    private async Task RunCaptureBridgeAsync(CancellationToken cancellationToken)
    {
        long lastSequence = -1;

        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var sequence = await TryReadCaptureSequenceAsync(cancellationToken);
                if (sequence is not null && sequence.Value != lastSequence)
                {
                    var frame = await TryReadValidGdFrameAsync(
                        _outgoingFramePath,
                        cancellationToken);

                    if (frame is not null)
                    {
                        // Keep the exact DeSmuME top-screen frame (normally 256x192)
                        // in the transport. Only the emulator overlay is downscaled.
                        lock (_latestFrameGate)
                        {
                            _latestOutgoingFrame = frame;
                            _latestOutgoingVersion++;
                            _lastCaptureAt = DateTimeOffset.UtcNow;
                        }

                        OutgoingFrameChanged?.Invoke(frame);
                        lastSequence = sequence.Value;
                        SetOutgoingStatus("Aufnahme und Stream laufen · 256×192");
                    }
                }

                DateTimeOffset lastCaptureAt;
                lock (_latestFrameGate)
                    lastCaptureAt = _lastCaptureAt;

                if (lastCaptureAt != DateTimeOffset.MinValue &&
                    DateTimeOffset.UtcNow - lastCaptureAt >= StaleFrameTimeout)
                {
                    SetOutgoingStatus("Stream läuft · DeSmuME liefert keine neuen Frames");
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                // Lua may replace the frame files while they are being polled.
            }
            catch (UnauthorizedAccessException)
            {
                SetOutgoingStatus("Stream läuft · Capture-Datei vorübergehend nicht lesbar");
            }
            catch (InvalidDataException)
            {
                SetOutgoingStatus("Stream läuft · ungültiger DeSmuME-Frame verworfen");
            }

            try
            {
                await Task.Delay(CapturePollInterval, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task RunOutgoingServerAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    var client = await listener.AcceptTcpClientAsync(cancellationToken);
                    _ = HandleClientSafelyAsync(client, cancellationToken);
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
                    await Task.Delay(ReconnectDelay, cancellationToken);
                }
            }
        }
        finally
        {
            listener.Stop();
        }
    }

    private async Task HandleClientSafelyAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        using (client)
        {
            try
            {
                await HandleClientAsync(client, cancellationToken);
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
            catch
            {
                // One viewer must never terminate the stream server.
            }
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        client.NoDelay = true;
        client.SendBufferSize = 512 * 1024;
        client.ReceiveBufferSize = 32 * 1024;

        await using var networkStream = client.GetStream();
        using var reader = new StreamReader(
            networkStream,
            Encoding.ASCII,
            detectEncodingFromByteOrderMarks: false,
            bufferSize: 1024,
            leaveOpen: true);

        var requestLine = await reader.ReadLineAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(requestLine))
            return;

        while (true)
        {
            var headerLine = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(headerLine))
                break;
        }

        var isStreamRequest =
            requestLine.StartsWith("GET /stream ", StringComparison.OrdinalIgnoreCase) ||
            requestLine.StartsWith("GET / ", StringComparison.OrdinalIgnoreCase);

        if (!isStreamRequest)
        {
            await WriteSimpleHttpResponseAsync(
                networkStream,
                404,
                "SoulBuddy stream endpoint not found.",
                cancellationToken);
            return;
        }

        var header =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/x-soulbuddy-gd-stream\r\n" +
            "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
            "Pragma: no-cache\r\n" +
            "Connection: keep-alive\r\n\r\n";
        await networkStream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
        await networkStream.FlushAsync(cancellationToken);

        long lastSentVersion = -1;
        var lastPacketAt = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            byte[]? frame = null;
            long version;

            lock (_latestFrameGate)
            {
                version = _latestOutgoingVersion;
                if (_latestOutgoingFrame is not null && version != lastSentVersion)
                    frame = _latestOutgoingFrame;
            }

            var now = DateTimeOffset.UtcNow;
            if (frame is not null)
            {
                await WriteFramePacketAsync(networkStream, frame, cancellationToken);
                lastSentVersion = version;
                lastPacketAt = now;
            }
            else if (lastPacketAt == DateTimeOffset.MinValue ||
                     now - lastPacketAt >= HeartbeatInterval)
            {
                await WriteHeartbeatAsync(networkStream, cancellationToken);
                lastPacketAt = now;
            }

            await Task.Delay(SendPollInterval, cancellationToken);
        }
    }

    private async Task RunIncomingClientAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunIncomingConnectionAsync(uri, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (TimeoutException)
            {
                SetIncomingStatus("Partner-Stream reagiert nicht · verbinde neu …");
                HideIncomingFrame();
            }
            catch (SocketException)
            {
                SetIncomingStatus("Partner-Stream getrennt · verbinde neu …");
                HideIncomingFrame();
            }
            catch (IOException)
            {
                SetIncomingStatus("Partner-Stream getrennt · verbinde neu …");
                HideIncomingFrame();
            }
            catch (InvalidDataException)
            {
                SetIncomingStatus("Partner-Stream liefert ungültige Videodaten");
                HideIncomingFrame();
            }
            catch (Exception ex)
            {
                SetIncomingStatus($"Streamfehler ({ex.GetType().Name}) · verbinde neu …");
                HideIncomingFrame();
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(ReconnectDelay, cancellationToken);
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    break;
                }
            }
        }
    }

    private async Task RunIncomingConnectionAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        var port = uri.IsDefaultPort ? 80 : uri.Port;
        using var client = new TcpClient();
        client.NoDelay = true;
        client.ReceiveBufferSize = 512 * 1024;
        client.SendBufferSize = 32 * 1024;
        await client.ConnectAsync(uri.Host, port, cancellationToken);

        await using var stream = client.GetStream();
        var pathAndQuery = string.IsNullOrWhiteSpace(uri.PathAndQuery)
            ? "/stream"
            : uri.PathAndQuery;
        var request =
            $"GET {pathAndQuery} HTTP/1.1\r\n" +
            $"Host: {uri.Host}:{port}\r\n" +
            "Accept: application/x-soulbuddy-gd-stream\r\n" +
            "Connection: keep-alive\r\n\r\n";
        await stream.WriteAsync(Encoding.ASCII.GetBytes(request), cancellationToken);
        await stream.FlushAsync(cancellationToken);

        var responseHeader = await ReadHttpHeaderAsync(stream, cancellationToken);
        var firstLine = responseHeader.Split("\r\n", StringSplitOptions.None)[0];
        if (!firstLine.Contains(" 200 ", StringComparison.Ordinal))
            throw new IOException($"Stream antwortet nicht erfolgreich: {firstLine}");

        SetIncomingStatus("Partner-Stream verbunden · warte auf Videoframes …");
        var lastFrameAt = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            var lengthBuffer = new byte[4];
            await ReadExactlyWithIdleTimeoutAsync(stream, lengthBuffer, cancellationToken);

            var frameLength =
                (lengthBuffer[0] << 24) |
                (lengthBuffer[1] << 16) |
                (lengthBuffer[2] << 8) |
                lengthBuffer[3];

            if (frameLength == 0)
            {
                if (lastFrameAt != DateTimeOffset.MinValue &&
                    DateTimeOffset.UtcNow - lastFrameAt >= StaleFrameTimeout)
                {
                    HideIncomingFrame();
                    SetIncomingStatus("Partner-Stream verbunden · kein Videosignal");
                }

                continue;
            }

            if (frameLength < 11 || frameLength > MaximumFrameBytes)
                throw new InvalidDataException("Ungültige Stream-Framegröße.");

            var frame = new byte[frameLength];
            await ReadExactlyWithIdleTimeoutAsync(stream, frame, cancellationToken);

            if (!TryGetGdDimensions(frame, out var width, out var height))
                throw new InvalidDataException("Stream liefert ungültige Videodaten.");

            // Preserve the full native frame for the SoulBuddy GUI. Only the
            // DeSmuME picture-in-picture bridge receives the 64x48 derivative.
            IncomingFrameChanged?.Invoke(frame);
            var overlayFrame = width == OverlayWidth && height == OverlayHeight
                ? frame
                : ResizeGdNearest(frame, OverlayWidth, OverlayHeight);

            lastFrameAt = DateTimeOffset.UtcNow;
            if (await TryWriteFrameAtomicallyAsync(
                    _incomingFramePath,
                    overlayFrame,
                    cancellationToken))
            {
                SetIncomingStatus($"Partner-Stream verbunden · {width}×{height}");
            }
            else
            {
                SetIncomingStatus("Partner-Stream verbunden · Overlay-Datei kurz belegt");
            }
        }
    }

    private void HideIncomingFrame()
    {
        TryDeleteFile(_incomingFramePath);
        IncomingFrameChanged?.Invoke(null);
    }

    private static async Task WriteFramePacketAsync(
        Stream stream,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        var length = frame.Length;
        var prefix = new byte[4]
        {
            (byte)(length >> 24),
            (byte)(length >> 16),
            (byte)(length >> 8),
            (byte)length
        };

        await stream.WriteAsync(prefix, cancellationToken);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task WriteHeartbeatAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        await stream.WriteAsync(new byte[4], cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private static async Task WriteSimpleHttpResponseAsync(
        Stream stream,
        int statusCode,
        string message,
        CancellationToken cancellationToken)
    {
        var body = Encoding.UTF8.GetBytes(message);
        var reason = statusCode == 404 ? "Not Found" : "Error";
        var header =
            $"HTTP/1.1 {statusCode} {reason}\r\n" +
            "Content-Type: text/plain; charset=utf-8\r\n" +
            $"Content-Length: {body.Length}\r\n" +
            "Connection: close\r\n\r\n";

        await stream.WriteAsync(Encoding.ASCII.GetBytes(header), cancellationToken);
        await stream.WriteAsync(body, cancellationToken);
        await stream.FlushAsync(cancellationToken);
    }

    private async Task<long?> TryReadCaptureSequenceAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(_outgoingSequencePath))
                return null;

            await using var stream = new FileStream(
                _outgoingSequencePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 64,
                useAsync: true);

            if (stream.Length <= 0 || stream.Length > 64)
                return null;

            var data = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(data, cancellationToken);
            var text = Encoding.ASCII.GetString(data).Trim();
            return long.TryParse(text, out var value) ? value : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static async Task<string> ReadHttpHeaderAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var terminator = new byte[] { 13, 10, 13, 10 };
        var matched = 0;
        var oneByte = new byte[1];

        while (memory.Length < MaximumHttpHeaderBytes)
        {
            await ReadExactlyWithIdleTimeoutAsync(stream, oneByte, cancellationToken);
            var value = oneByte[0];
            memory.WriteByte(value);

            if (value == terminator[matched])
            {
                matched++;
                if (matched == terminator.Length)
                    return Encoding.ASCII.GetString(memory.ToArray());
            }
            else
            {
                matched = value == terminator[0] ? 1 : 0;
            }
        }

        throw new InvalidDataException("Stream-HTTP-Header ist zu groß.");
    }

    private static async Task ReadExactlyWithIdleTimeoutAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(StreamIdleTimeout);

        var offset = 0;
        try
        {
            while (offset < buffer.Length)
            {
                var read = await stream.ReadAsync(
                    buffer.AsMemory(offset, buffer.Length - offset),
                    timeout.Token);
                if (read == 0)
                    throw new EndOfStreamException("Der Stream wurde geschlossen.");
                offset += read;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Der Stream hat zu lange keine Daten geliefert.");
        }
    }

    private async Task StopIncomingAsync(bool deleteFrame)
    {
        var cancellation = _incomingCancellation;
        var task = _incomingTask;
        _incomingCancellation = null;
        _incomingTask = null;

        if (cancellation is not null)
        {
            cancellation.Cancel();
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
            cancellation.Dispose();
        }

        if (deleteFrame)
            HideIncomingFrame();
    }

    private async Task StopOutgoingCoreAsync()
    {
        var cancellation = _outgoingCancellation;
        var serverTask = _outgoingServerTask;
        var captureTask = _captureBridgeTask;
        var listener = _listener;

        _outgoingCancellation = null;
        _outgoingServerTask = null;
        _captureBridgeTask = null;
        _listener = null;
        OutgoingUrl = null;

        if (cancellation is not null)
            cancellation.Cancel();
        listener?.Stop();

        foreach (var task in new[] { serverTask, captureTask })
        {
            if (task is null)
                continue;

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
        }

        cancellation?.Dispose();
        TryDeleteFile(_captureEnabledPath);
        TryDeleteFile(_outgoingFramePath);
        TryDeleteFile(_outgoingSequencePath);

        lock (_latestFrameGate)
        {
            _latestOutgoingFrame = null;
            _latestOutgoingVersion = 0;
            _lastCaptureAt = DateTimeOffset.MinValue;
        }

        OutgoingFrameChanged?.Invoke(null);
        SetOutgoingStatus("Stream nicht gestartet");
    }

    private static async Task<byte[]?> TryReadValidGdFrameAsync(
        string path,
        CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            await using var stream = new FileStream(
                path,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 256 * 1024,
                useAsync: true);

            if (stream.Length < 11 || stream.Length > MaximumFrameBytes)
                return null;

            var data = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(data, cancellationToken);
            return TryGetGdDimensions(data, out _, out _) ? data : null;
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
    }

    private static bool TryGetGdDimensions(byte[] data, out int width, out int height)
    {
        width = 0;
        height = 0;

        if (data.Length < 11 ||
            data[0] != 0xFF ||
            data[1] != 0xFE ||
            data[6] != 1)
        {
            return false;
        }

        width = (data[2] << 8) | data[3];
        height = (data[4] << 8) | data[5];
        if (width <= 0 || height <= 0)
            return false;

        return 11L + ((long)width * height * 4L) == data.Length;
    }

    private static byte[] ResizeGdNearest(
        byte[] source,
        int targetWidth,
        int targetHeight)
    {
        if (!TryGetGdDimensions(source, out var sourceWidth, out var sourceHeight))
            throw new InvalidDataException("Ungültiger GD-Videoframe.");

        var result = new byte[11 + (targetWidth * targetHeight * 4)];
        result[0] = 0xFF;
        result[1] = 0xFE;
        result[2] = (byte)(targetWidth >> 8);
        result[3] = (byte)targetWidth;
        result[4] = (byte)(targetHeight >> 8);
        result[5] = (byte)targetHeight;
        result[6] = 1;
        result[7] = 0xFF;
        result[8] = 0xFF;
        result[9] = 0xFF;
        result[10] = 0xFF;

        for (var y = 0; y < targetHeight; y++)
        {
            var sourceY = Math.Min(
                sourceHeight - 1,
                (int)((long)y * sourceHeight / targetHeight));

            for (var x = 0; x < targetWidth; x++)
            {
                var sourceX = Math.Min(
                    sourceWidth - 1,
                    (int)((long)x * sourceWidth / targetWidth));
                var sourceOffset = 11 + ((sourceY * sourceWidth + sourceX) * 4);
                var targetOffset = 11 + ((y * targetWidth + x) * 4);
                Buffer.BlockCopy(source, sourceOffset, result, targetOffset, 4);
            }
        }

        return result;
    }

    private static async Task<bool> TryWriteFrameAtomicallyAsync(
        string path,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = path + $".tmp.{Environment.ProcessId}";
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                await File.WriteAllBytesAsync(temporaryPath, data, cancellationToken);
                File.Move(temporaryPath, path, overwrite: true);
                return true;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (IOException)
            {
                TryDeleteFile(temporaryPath);
            }
            catch (UnauthorizedAccessException)
            {
                TryDeleteFile(temporaryPath);
            }

            if (attempt < 2)
                await Task.Delay(5, cancellationToken);
        }

        TryDeleteFile(temporaryPath);
        return false;
    }

    private void SetIncomingStatus(string value)
    {
        if (string.Equals(_incomingStatus, value, StringComparison.Ordinal))
            return;

        _incomingStatus = value;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private void SetOutgoingStatus(string value)
    {
        if (string.Equals(_outgoingStatus, value, StringComparison.Ordinal))
            return;

        _outgoingStatus = value;
        StatusChanged?.Invoke(this, EventArgs.Empty);
    }

    private static void TryDeleteFile(string path)
    {
        try
        {
            if (File.Exists(path))
                File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static string FindRuntimeDirectory()
    {
        var searchRoots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var root in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directory = new DirectoryInfo(Path.GetFullPath(root));
            while (directory is not null)
            {
                if (File.Exists(Path.Combine(directory.FullName, "appsettings.json")) &&
                    Directory.Exists(Path.Combine(
                        directory.FullName,
                        "collectors",
                        "desmume-gen4")))
                {
                    return Path.Combine(directory.FullName, "runtime");
                }

                directory = directory.Parent;
            }
        }

        return Path.Combine(Directory.GetCurrentDirectory(), "runtime");
    }

    public async ValueTask DisposeAsync()
    {
        await StopIncomingAsync(deleteFrame: true);
        await StopOutgoingAsync();
        _outgoingGate.Dispose();
    }
}
