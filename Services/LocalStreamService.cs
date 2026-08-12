using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;

namespace SoulBuddy.Services;

internal sealed class LocalStreamService : IAsyncDisposable
{
    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(67);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(250);
    private const int MaximumFrameBytes = 2 * 1024 * 1024;
    private const int OverlayWidth = 64;
    private const int OverlayHeight = 48;

    private readonly HttpClient _httpClient = new()
    {
        // The local stream is intentionally long-lived. Per-request timeouts would
        // tear down a healthy video connection after a couple of seconds.
        Timeout = Timeout.InfiniteTimeSpan
    };
    private readonly SemaphoreSlim _outgoingGate = new(1, 1);
    private readonly string _captureEnabledPath;
    private readonly string _outgoingFramePath;
    private readonly string _incomingFramePath;

    private TcpListener? _listener;
    private CancellationTokenSource? _outgoingCancellation;
    private Task? _outgoingTask;
    private CancellationTokenSource? _incomingCancellation;
    private Task? _incomingTask;
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
        _incomingFramePath = LuaLaunchContext.ScopePath(
            Path.Combine(runtimeDirectory, "stream-in.gd"));
    }

    public event EventHandler? StatusChanged;

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

            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();

            var endpoint = (IPEndPoint)listener.LocalEndpoint;
            OutgoingUrl = $"http://127.0.0.1:{endpoint.Port}/stream";
            _listener = listener;
            _outgoingCancellation = new CancellationTokenSource();

            await File.WriteAllTextAsync(
                _captureEnabledPath,
                "1",
                cancellationToken);

            SetOutgoingStatus("Aufnahme und Stream laufen");
            _outgoingTask = RunOutgoingServerAsync(
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
            return;
        }

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            SetIncomingStatus("Ungültige Stream-Adresse");
            return;
        }

        if (string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/")
            uri = new Uri(uri, "/stream");

        _incomingCancellation = new CancellationTokenSource();
        SetIncomingStatus("Verbindung zum Stream wird hergestellt …");
        _incomingTask = RunIncomingClientAsync(uri, _incomingCancellation.Token);
    }

    private async Task RunOutgoingServerAsync(
        TcpListener listener,
        CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await listener.AcceptTcpClientAsync(cancellationToken);
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

                _ = HandleClientSafelyAsync(client, cancellationToken);
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
                // The viewer disconnected. The listener remains alive for a reconnect.
            }
            catch (SocketException)
            {
                // The viewer disconnected. The listener remains alive for a reconnect.
            }
        }
    }

    private async Task HandleClientAsync(
        TcpClient client,
        CancellationToken cancellationToken)
    {
        client.NoDelay = true;
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

        // Keep one HTTP connection open and send all video frames over it. The first
        // prototype created a brand-new TCP connection for every frame, which caused
        // unnecessary socket churn and could make the stream stall after a short time.
        var header =
            "HTTP/1.1 200 OK\r\n" +
            "Content-Type: application/x-soulbuddy-gd-stream\r\n" +
            "Transfer-Encoding: chunked\r\n" +
            "Cache-Control: no-store, no-cache, must-revalidate\r\n" +
            "Pragma: no-cache\r\n" +
            "Connection: keep-alive\r\n\r\n";
        await networkStream.WriteAsync(
            Encoding.ASCII.GetBytes(header),
            cancellationToken);
        await networkStream.FlushAsync(cancellationToken);

        while (!cancellationToken.IsCancellationRequested)
        {
            var frame = await TryReadValidGdFrameAsync(
                _outgoingFramePath,
                cancellationToken);

            if (frame is not null)
            {
                await WriteChunkedFrameAsync(
                    networkStream,
                    frame,
                    cancellationToken);
            }

            await Task.Delay(FrameInterval, cancellationToken);
        }
    }

    private static async Task WriteChunkedFrameAsync(
        Stream stream,
        byte[] frame,
        CancellationToken cancellationToken)
    {
        var payloadLength = checked(frame.Length + 4);
        var chunkHeader = Encoding.ASCII.GetBytes($"{payloadLength:X}\r\n");
        var frameLength = new byte[4]
        {
            (byte)(frame.Length >> 24),
            (byte)(frame.Length >> 16),
            (byte)(frame.Length >> 8),
            (byte)frame.Length
        };
        var chunkEnd = "\r\n"u8.ToArray();

        await stream.WriteAsync(chunkHeader, cancellationToken);
        await stream.WriteAsync(frameLength, cancellationToken);
        await stream.WriteAsync(frame, cancellationToken);
        await stream.WriteAsync(chunkEnd, cancellationToken);
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

    private async Task RunIncomingClientAsync(
        Uri uri,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                using var request = new HttpRequestMessage(HttpMethod.Get, uri);
                using var response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken);

                if (!response.IsSuccessStatusCode)
                {
                    SetIncomingStatus($"Stream nicht erreichbar ({(int)response.StatusCode})");
                }
                else
                {
                    await using var stream = await response.Content.ReadAsStreamAsync(
                        cancellationToken);
                    SetIncomingStatus("Stream verbunden · warte auf Videoframes …");

                    while (!cancellationToken.IsCancellationRequested)
                    {
                        var lengthBuffer = new byte[4];
                        if (!await ReadExactlyOrEndAsync(
                                stream,
                                lengthBuffer,
                                cancellationToken))
                        {
                            break;
                        }

                        var frameLength =
                            (lengthBuffer[0] << 24) |
                            (lengthBuffer[1] << 16) |
                            (lengthBuffer[2] << 8) |
                            lengthBuffer[3];

                        if (frameLength < 11 || frameLength > MaximumFrameBytes)
                            throw new InvalidDataException("Ungültige Stream-Framegröße.");

                        var frame = new byte[frameLength];
                        if (!await ReadExactlyOrEndAsync(
                                stream,
                                frame,
                                cancellationToken))
                        {
                            throw new EndOfStreamException(
                                "Der Stream wurde mitten in einem Frame beendet.");
                        }

                        if (!TryGetGdDimensions(frame, out _, out _))
                            throw new InvalidDataException("Stream liefert ungültige Videodaten.");

                        var overlayFrame = ResizeGdNearest(
                            frame,
                            OverlayWidth,
                            OverlayHeight);
                        await WriteFrameAtomicallyAsync(
                            _incomingFramePath,
                            overlayFrame,
                            cancellationToken);

                        SetIncomingStatus("Stream wird im Overlay angezeigt");
                    }
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (HttpRequestException)
            {
                SetIncomingStatus("Stream getrennt · Verbindung wird wiederhergestellt …");
            }
            catch (IOException)
            {
                SetIncomingStatus("Stream getrennt · Verbindung wird wiederhergestellt …");
            }
            catch (InvalidDataException)
            {
                SetIncomingStatus("Stream liefert ungültige Videodaten · neuer Verbindungsversuch …");
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

    private static async Task<bool> ReadExactlyOrEndAsync(
        Stream stream,
        byte[] buffer,
        CancellationToken cancellationToken)
    {
        var offset = 0;
        while (offset < buffer.Length)
        {
            var read = await stream.ReadAsync(
                buffer.AsMemory(offset, buffer.Length - offset),
                cancellationToken);
            if (read == 0)
                return false;

            offset += read;
        }

        return true;
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
            TryDeleteFile(_incomingFramePath);
    }

    private async Task StopOutgoingCoreAsync()
    {
        var cancellation = _outgoingCancellation;
        var task = _outgoingTask;
        var listener = _listener;

        _outgoingCancellation = null;
        _outgoingTask = null;
        _listener = null;
        OutgoingUrl = null;

        if (cancellation is not null)
            cancellation.Cancel();
        listener?.Stop();

        if (task is not null)
        {
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
                bufferSize: 64 * 1024,
                useAsync: true);

            if (stream.Length < 11 || stream.Length > MaximumFrameBytes)
                return null;

            var data = new byte[checked((int)stream.Length)];
            await stream.ReadExactlyAsync(data, cancellationToken);
            return TryGetGdDimensions(data, out _, out _)
                ? data
                : null;
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

    private static bool TryGetGdDimensions(
        byte[] data,
        out int width,
        out int height)
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

        var expectedLength = 11L + ((long)width * height * 4L);
        return expectedLength == data.Length;
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

    private static async Task WriteFrameAtomicallyAsync(
        string path,
        byte[] data,
        CancellationToken cancellationToken)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrWhiteSpace(directory))
            Directory.CreateDirectory(directory);

        var temporaryPath = path + $".tmp.{Environment.ProcessId}";
        await File.WriteAllBytesAsync(temporaryPath, data, cancellationToken);
        File.Move(temporaryPath, path, overwrite: true);
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
        _httpClient.Dispose();
        _outgoingGate.Dispose();
    }
}
