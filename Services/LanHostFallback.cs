using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using Avalonia.Threading;

namespace SoulBuddy.Services;

internal static class LanHostFallback
{
    private static DispatcherTimer? _timer;
    private static bool _scanRunning;
    private static DateTimeOffset _nextScanAt = DateTimeOffset.MinValue;
    private static string _lastSessionId = string.Empty;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _timer.Tick += (_, _) => TryStartScan();
            _timer.Start();
        });
    }

    private static void TryStartScan()
    {
        var service = SoulBuddyNetworkService.Current;
        if (service is null || service.Mode != SoulBuddyNetworkMode.Join ||
            service.State == SoulBuddyNetworkState.Connected || _scanRunning ||
            string.IsNullOrWhiteSpace(service.SessionId) ||
            DateTimeOffset.UtcNow < _nextScanAt)
        {
            return;
        }

        if (!string.Equals(_lastSessionId, service.SessionId, StringComparison.Ordinal))
        {
            _lastSessionId = service.SessionId;
            _nextScanAt = DateTimeOffset.UtcNow.AddSeconds(2);
            return;
        }

        _scanRunning = true;
        _nextScanAt = DateTimeOffset.UtcNow.AddSeconds(8);
        _ = ScanAndConnectAsync(service);
    }

    private static async Task ScanAndConnectAsync(SoulBuddyNetworkService service)
    {
        try
        {
            var candidates = GetCandidateAddresses();
            using var scanSource = new CancellationTokenSource(TimeSpan.FromSeconds(6));
            using var gate = new SemaphoreSlim(32, 32);
            var foundSource = new TaskCompletionSource<IPAddress?>(TaskCreationOptions.RunContinuationsAsynchronously);

            var tasks = candidates.Select(async address =>
            {
                await gate.WaitAsync(scanSource.Token);
                try
                {
                    if (foundSource.Task.IsCompleted) return;
                    using var client = new TcpClient();
                    using var timeout = CancellationTokenSource.CreateLinkedTokenSource(scanSource.Token);
                    timeout.CancelAfter(TimeSpan.FromMilliseconds(350));
                    await client.ConnectAsync(address, SoulBuddyNetworkService.DefaultTcpPort, timeout.Token);
                    foundSource.TrySetResult(address);
                }
                catch
                {
                }
                finally
                {
                    gate.Release();
                }
            }).ToArray();

            await Task.WhenAny(
                foundSource.Task,
                Task.WhenAll(tasks),
                Task.Delay(TimeSpan.FromSeconds(6), scanSource.Token));

            var found = foundSource.Task.IsCompletedSuccessfully ? foundSource.Task.Result : null;
            if (found is null)
                return;

            if (service.Mode != SoulBuddyNetworkMode.Join ||
                service.State == SoulBuddyNetworkState.Connected)
            {
                return;
            }

            var endpoint = $"{found}:{SoulBuddyNetworkService.DefaultTcpPort}";
            service.PrepareJoin(service.SessionId, service.PlayerName, endpoint);
        }
        catch (OperationCanceledException)
        {
        }
        catch
        {
        }
        finally
        {
            _scanRunning = false;
        }
    }

    private static IReadOnlyList<IPAddress> GetCandidateAddresses()
    {
        var result = new HashSet<IPAddress>();
        foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
        {
            if (nic.OperationalStatus != OperationalStatus.Up ||
                nic.NetworkInterfaceType == NetworkInterfaceType.Loopback)
            {
                continue;
            }

            foreach (var unicast in nic.GetIPProperties().UnicastAddresses)
            {
                var address = unicast.Address;
                var mask = unicast.IPv4Mask;
                if (address.AddressFamily != AddressFamily.InterNetwork ||
                    mask is null ||
                    IPAddress.IsLoopback(address))
                {
                    continue;
                }

                var a = address.GetAddressBytes();
                var m = mask.GetAddressBytes();
                var networkBytes = new byte[4];
                var broadcastBytes = new byte[4];
                for (var i = 0; i < 4; i++)
                {
                    networkBytes[i] = (byte)(a[i] & m[i]);
                    broadcastBytes[i] = (byte)(networkBytes[i] | ~m[i]);
                }

                var network = ToUInt32(networkBytes);
                var broadcast = ToUInt32(broadcastBytes);
                var own = ToUInt32(a);
                if (broadcast - network > 1024)
                {
                    network = own & 0xFFFFFF00u;
                    broadcast = network | 0xFFu;
                }

                for (var value = network + 1; value < broadcast; value++)
                {
                    if (value != own)
                        result.Add(FromUInt32(value));
                }
            }
        }

        return result.ToArray();
    }

    private static uint ToUInt32(byte[] b) =>
        ((uint)b[0] << 24) | ((uint)b[1] << 16) | ((uint)b[2] << 8) | b[3];

    private static IPAddress FromUInt32(uint v) =>
        new(new byte[] { (byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v });
}
