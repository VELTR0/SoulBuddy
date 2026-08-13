using System.Runtime.CompilerServices;

namespace SoulBuddy.Services;

/// <summary>
/// Tracks real incoming frame writes for the DeSmuME overlay. The sidecar sequence
/// changes only when a new frame is written, while the alive marker remains present
/// for exactly three seconds after the last observed frame. Lua can therefore keep
/// rendering the last valid frame across atomic file replacement and reconnect gaps
/// without extending the real no-signal timeout.
/// </summary>
internal static class IncomingStreamFrameTimeoutGuard
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(50);
    private static readonly string RuntimeDirectory = FindRuntimeDirectory();
    private static string IncomingFramePath => LuaLaunchContext.ScopePath(
        Path.Combine(RuntimeDirectory, "stream-in.gd"));
    private static string IncomingSequencePath => LuaLaunchContext.ScopePath(
        Path.Combine(RuntimeDirectory, "stream-in.seq"));
    private static string IncomingAlivePath => LuaLaunchContext.ScopePath(
        Path.Combine(RuntimeDirectory, "stream-in.alive"));

    private static Timer? _timer;
    private static DateTime _lastWriteUtc = DateTime.MinValue;
    private static DateTime _lastFrameObservedAtUtc = DateTime.MinValue;
    private static long _sequence;
    private static int _isRunning;

    [ModuleInitializer]
    internal static void Initialize()
    {
        Directory.CreateDirectory(RuntimeDirectory);
        TryDeleteFile(IncomingSequencePath);
        TryDeleteFile(IncomingAlivePath);

        _timer = new Timer(
            static _ => Tick(),
            null,
            TimeSpan.Zero,
            CheckInterval);
    }

    private static void Tick()
    {
        if (Interlocked.Exchange(ref _isRunning, 1) != 0)
            return;

        try
        {
            var now = DateTime.UtcNow;
            ObserveFrameWrite(now);
            ApplyTimeout(now);
        }
        finally
        {
            Volatile.Write(ref _isRunning, 0);
        }
    }

    private static void ObserveFrameWrite(DateTime now)
    {
        try
        {
            if (!File.Exists(IncomingFramePath))
                return;

            var writeUtc = File.GetLastWriteTimeUtc(IncomingFramePath);
            if (writeUtc == _lastWriteUtc)
                return;

            _lastWriteUtc = writeUtc;
            _lastFrameObservedAtUtc = now;
            _sequence++;

            WriteTextAtomically(IncomingSequencePath, _sequence.ToString());
            EnsureAliveMarker();
        }
        catch (FileNotFoundException)
        {
            // The receiver may be replacing the frame at this exact moment.
        }
        catch (IOException)
        {
            // A transient file lock is not a lost video signal.
        }
        catch (UnauthorizedAccessException)
        {
            // Retry on the next 50 ms tick.
        }
    }

    private static void ApplyTimeout(DateTime now)
    {
        if (_lastFrameObservedAtUtc == DateTime.MinValue ||
            now - _lastFrameObservedAtUtc < FrameTimeout)
        {
            return;
        }

        // Removing the marker is the authoritative three-second no-signal event.
        // Keep the last GD file on disk: Lua owns the visual cache, and a reconnect
        // must never race a timeout cleanup that deletes a newly arrived frame.
        TryDeleteFile(IncomingAlivePath);
    }

    private static void EnsureAliveMarker()
    {
        try
        {
            if (!File.Exists(IncomingAlivePath))
                File.WriteAllText(IncomingAlivePath, "1");
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private static void WriteTextAtomically(string path, string value)
    {
        var temporaryPath = path + ".tmp";
        try
        {
            File.WriteAllText(temporaryPath, value);
            File.Move(temporaryPath, path, true);
        }
        catch (IOException)
        {
            TryDeleteFile(temporaryPath);
        }
        catch (UnauthorizedAccessException)
        {
            TryDeleteFile(temporaryPath);
        }
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
}
