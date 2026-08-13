using System.Net.Sockets;
using System.Text;

namespace SoulBuddy.Services;

/// <summary>
/// Lightweight in-memory viewer for SoulBuddy's framed local/LAN video stream.
/// It is used only for the previews inside the Stream tab and never writes frames
/// to the DeSmuME runtime bridge.
/// </summary>
internal sealed class StreamPreviewClient : IAsyncDisposable
{
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromMilliseconds(300);
    private static readonly TimeSpan StreamIdleTimeout = TimeSpan.FromSeconds(3);
    private const int MaximumHeaderBytes = 16 * 1024;
    private const int MaximumFrameBytes = 2 * 1024 * 1024;

    private CancellationTokenSource? _cancellation;
    private Task? _task;
    private byte[]? _latestFrame;

    public event Action<byte[]?>? FrameChanged;

    public byte[]? LatestFrame => _latestFrame;

    public async Task ConnectAsync(string? value)
    {
        await DisconnectAsync();

        var text = value?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(text))
            return;

        if (!Uri.TryCreate(text, UriKind.Absolute, out var uri) ||
            uri.Scheme != Uri.UriSchemeHttp)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(uri.AbsolutePath) || uri.AbsolutePath == "/")
            uri = new Uri(uri, "/stream");

        _cancellation = new CancellationTokenSource();
        _task = RunAsync(uri, _cancellation.Token);
    }

    public async Task DisconnectAsync()
    {
        var cancellation = _cancellation;
        var task = _task;
        _cancellation = null;
        _task = null;

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

        PublishFrame(null);
    }

    private async Task RunAsync(Uri uri, CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                await RunConnectionAsync(uri, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (SocketException)
            {
                PublishFrame(null);
            }
            catch (IOException)
            {
                PublishFrame(null);
            }
            catch (InvalidDataException)
            {
                PublishFrame(null);
            }
            catch (TimeoutException)
            {
                PublishFrame(null);
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

    private async Task RunConnectionAsync(Uri uri, CancellationToken cancellationToken)
    {
        var port = uri.IsDefaultPort ? 80 : uri.Port;
        using var client = new TcpClient();
        client.NoDelay = true;
        client.ReceiveBufferSize = 64 * 1024;
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

        var header = await ReadHttpHeaderAsync(stream, cancellationToken);
        var firstLine = header.Split("\r\n", StringSplitOptions.None)[0];
        if (!firstLine.Contains(" 200 ", StringComparison.Ordinal))
            throw new IOException($"Stream antwortet nicht erfolgreich: {firstLine}");

        var lastFrameAt = DateTimeOffset.MinValue;

        while (!cancellationToken.IsCancellationRequested)
        {
            var lengthBuffer = new byte[4];
            await ReadExactlyWithTimeoutAsync(stream, lengthBuffer, cancellationToken);

            var frameLength =
                (lengthBuffer[0] << 24) |
                (lengthBuffer[1] << 16) |
                (lengthBuffer[2] << 8) |
                lengthBuffer[3];

            if (frameLength == 0)
            {
                if (lastFrameAt != DateTimeOffset.MinValue &&
                    DateTimeOffset.UtcNow - lastFrameAt >= StreamIdleTimeout)
                {
                    PublishFrame(null);
                }

                continue;
            }

            if (frameLength < 11 || frameLength > MaximumFrameBytes)
                throw new InvalidDataException("Ungültige Stream-Framegröße.");

            var frame = new byte[frameLength];
            await ReadExactlyWithTimeoutAsync(stream, frame, cancellationToken);

            if (!IsValidGdFrame(frame))
                throw new InvalidDataException("Ungültiger GD-Frame.");

            lastFrameAt = DateTimeOffset.UtcNow;
            PublishFrame(frame);
        }
    }

    private static bool IsValidGdFrame(byte[] data)
    {
        if (data.Length < 11 ||
            data[0] != 0xFF ||
            data[1] != 0xFE ||
            data[6] != 1)
        {
            return false;
        }

        var width = (data[2] << 8) | data[3];
        var height = (data[4] << 8) | data[5];
        if (width <= 0 || height <= 0)
            return false;

        return 11L + ((long)width * height * 4L) == data.Length;
    }

    private static async Task<string> ReadHttpHeaderAsync(
        Stream stream,
        CancellationToken cancellationToken)
    {
        using var memory = new MemoryStream();
        var terminator = new byte[] { 13, 10, 13, 10 };
        var matched = 0;
        var oneByte = new byte[1];

        while (memory.Length < MaximumHeaderBytes)
        {
            await ReadExactlyWithTimeoutAsync(stream, oneByte, cancellationToken);
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

        throw new InvalidDataException("Stream-Header ist zu groß.");
    }

    private static async Task ReadExactlyWithTimeoutAsync(
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
                    throw new EndOfStreamException("Stream wurde geschlossen.");

                offset += read;
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            throw new TimeoutException("Stream hat zu lange keine Daten geliefert.");
        }
    }

    private void PublishFrame(byte[]? frame)
    {
        if (frame is null && _latestFrame is null)
            return;

        _latestFrame = frame;
        FrameChanged?.Invoke(frame);
    }

    public async ValueTask DisposeAsync()
    {
        await DisconnectAsync();
    }
}
