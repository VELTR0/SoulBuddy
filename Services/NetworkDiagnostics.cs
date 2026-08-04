using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Reflection;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using SoulBuddy.Views;

namespace SoulBuddy.Services;

internal static class NetworkDiagnostics
{
    private static readonly object Sync = new();
    private static readonly HashSet<SoulBuddyNetworkService> Attached = [];
    private static readonly FieldInfo? ContextField = typeof(MainWindow).GetField(
        "_sessionContext",
        BindingFlags.Instance | BindingFlags.NonPublic);
    private static readonly FieldInfo? NetworkField = typeof(MainWindow).GetField(
        "_networkService",
        BindingFlags.Instance | BindingFlags.NonPublic);

    private static readonly string LogDirectory = Path.Combine(
        AppContext.BaseDirectory,
        "runtime");
    private static readonly string LogPath = Path.Combine(
        LogDirectory,
        $"network-debug-{Environment.ProcessId}.log");

    private static DispatcherTimer? _timer;
    private static int _tick;

    [ModuleInitializer]
    internal static void Initialize()
    {
        try
        {
            Directory.CreateDirectory(LogDirectory);
            File.WriteAllText(LogPath, string.Empty);
        }
        catch
        {
        }

        Log("BOOT", $"SoulBuddy network diagnostics started. pid={Environment.ProcessId} os={Environment.OSVersion} framework={Environment.Version} base={AppContext.BaseDirectory}");
        LogNetworkInterfaces();

        Dispatcher.UIThread.Post(() =>
        {
            _timer = new DispatcherTimer
            {
                Interval = TimeSpan.FromSeconds(1)
            };
            _timer.Tick += (_, _) => Poll();
            _timer.Start();
            Poll();
        });
    }

    private static void Poll()
    {
        try
        {
            if (Application.Current?.ApplicationLifetime is not
                IClassicDesktopStyleApplicationLifetime desktop)
            {
                return;
            }

            foreach (var window in desktop.Windows.OfType<MainWindow>())
            {
                if (NetworkField?.GetValue(window) is not SoulBuddyNetworkService network)
                {
                    continue;
                }

                if (Attached.Add(network))
                {
                    network.StatusChanged += (_, _) => LogService("STATUS", network);
                    LogService("ATTACH", network);

                    var context = ContextField?.GetValue(window);
                    Log("CONTEXT", context?.ToString() ?? "SessionContext attached (details available through service state after start)");
                }
            }

            _tick++;
            if (_tick % 5 == 0)
            {
                foreach (var network in Attached)
                {
                    LogService("POLL", network);
                }

                LogSockets();
            }
        }
        catch (Exception ex)
        {
            Log("DIAG-ERROR", ex.ToString());
        }
    }

    private static void LogService(string source, SoulBuddyNetworkService service)
    {
        Log(source,
            $"mode={service.Mode} state={service.State} session='{service.SessionId}' player='{service.PlayerName}' remote='{service.RemotePlayerName}' joinAddress='{service.JoinAddress}' internet='{service.InternetAddress}' status='{service.StatusText}' snapshot={(service.LatestRemoteSnapshot is null ? "none" : "present")}");
    }

    private static void LogNetworkInterfaces()
    {
        try
        {
            foreach (var nic in NetworkInterface.GetAllNetworkInterfaces())
            {
                var addresses = nic.GetIPProperties().UnicastAddresses
                    .Select(item => item.Address)
                    .Where(address => address.AddressFamily is AddressFamily.InterNetwork or AddressFamily.InterNetworkV6)
                    .Select(address => address.ToString());

                Log("NIC",
                    $"name='{nic.Name}' type={nic.NetworkInterfaceType} status={nic.OperationalStatus} addresses=[{string.Join(", ", addresses)}]");
            }
        }
        catch (Exception ex)
        {
            Log("NIC-ERROR", ex.Message);
        }
    }

    private static void LogSockets()
    {
        try
        {
            var properties = IPGlobalProperties.GetIPGlobalProperties();

            foreach (var listener in properties.GetActiveTcpListeners()
                         .Where(endpoint => endpoint.Port == SoulBuddyNetworkService.DefaultTcpPort))
            {
                Log("TCP-LISTEN", listener.ToString());
            }

            foreach (var connection in properties.GetActiveTcpConnections()
                         .Where(item => item.LocalEndPoint.Port == SoulBuddyNetworkService.DefaultTcpPort ||
                                        item.RemoteEndPoint.Port == SoulBuddyNetworkService.DefaultTcpPort))
            {
                Log("TCP-CONNECTION",
                    $"local={connection.LocalEndPoint} remote={connection.RemoteEndPoint} state={connection.State}");
            }

            foreach (var listener in properties.GetActiveUdpListeners()
                         .Where(endpoint => endpoint.Port == 45832))
            {
                Log("UDP-LISTEN", listener.ToString());
            }
        }
        catch (Exception ex)
        {
            Log("SOCKET-ERROR", ex.Message);
        }
    }

    private static void Log(string source, string message)
    {
        var line = $"[{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss.fff zzz}] [SoulBuddy Net] [{source}] {message}";

        try
        {
            Console.WriteLine(line);
            Console.Error.WriteLine(line);
        }
        catch
        {
        }

        try
        {
            lock (Sync)
            {
                Directory.CreateDirectory(LogDirectory);
                File.AppendAllText(LogPath, line + Environment.NewLine);
            }
        }
        catch
        {
        }
    }
}
