using System.Runtime.CompilerServices;

namespace SoulBuddy.Services;

/// <summary>
/// Removes stale incoming stream frames so DeSmuME does not keep rendering the
/// last partner image after the sender has stopped or disappeared from the network.
/// </summary>
internal static class IncomingStreamFrameTimeoutGuard
{
    private static readonly TimeSpan FrameTimeout = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(500);
    private static readonly string RuntimeDirectory = FindRuntimeDirectory();
    private static Timer? _timer;

    [ModuleInitializer]
    internal static void Initialize()
    {
        _timer = new Timer(
            static _ => RemoveStaleIncomingFrames(),
            null,
            CheckInterval,
            CheckInterval);
    }

    private static void RemoveStaleIncomingFrames()
    {
        try
        {
            if (!Directory.Exists(RuntimeDirectory))
                return;

            var now = DateTime.UtcNow;
            foreach (var path in Directory.EnumerateFiles(
                         RuntimeDirectory,
                         "stream-in*.gd",
                         SearchOption.TopDirectoryOnly))
            {
                try
                {
                    var lastWriteUtc = File.GetLastWriteTimeUtc(path);
                    if (now - lastWriteUtc < FrameTimeout)
                        continue;

                    File.Delete(path);
                }
                catch (FileNotFoundException)
                {
                    // The stream writer replaced/deleted the frame between checks.
                }
                catch (IOException)
                {
                    // Lua may briefly have the frame open. Retry on the next tick.
                }
                catch (UnauthorizedAccessException)
                {
                    // Retry later; never let stream cleanup affect SoulBuddy itself.
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
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
