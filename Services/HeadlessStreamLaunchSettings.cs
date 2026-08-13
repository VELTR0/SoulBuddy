namespace SoulBuddy.Services;

public static class HeadlessStreamLaunchSettings
{
    private static readonly object Sync = new();
    private static bool _enabled;

    public static bool Enabled
    {
        get
        {
            lock (Sync)
                return _enabled;
        }
    }

    public static void Configure(bool enabled)
    {
        lock (Sync)
            _enabled = enabled;
    }
}
