using Avalonia;
using Avalonia.Threading;
using SoulBuddy.Services;

namespace SoulBuddy;

internal static class Program
{
    // Keep the names valid on both Windows and Unix-based systems such as macOS.
    // Lua-started instances append their unique launch token so multiple DeSmuME
    // processes can use the same checkout at the same time.
    private const string SingleInstanceMutexName = "SoulBuddy.SingleInstance";
    private const string ShowSetupEventName = "SoulBuddy.ShowSetup";

    [STAThread]
    public static void Main(string[] args)
    {
        LuaLaunchContext.Initialize(args);
        var singleInstanceMutexName = LuaLaunchContext.InstanceName(SingleInstanceMutexName);
        var showSetupEventName = LuaLaunchContext.InstanceName(ShowSetupEventName);

        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: singleInstanceMutexName,
            createdNew: out var createdNew);

        if (!createdNew)
        {
            if (LuaLaunchContext.FromLua)
                TrySignalExistingInstance(showSetupEventName);

            return;
        }

        using var showSetupEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: showSetupEventName);

        var listenerThread = new Thread(() => ListenForSetupRequests(showSetupEvent))
        {
            IsBackground = true,
            Name = "SoulBuddy setup request listener"
        };
        listenerThread.Start();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static void TrySignalExistingInstance(string showSetupEventName)
    {
        try
        {
            using var showSetupEvent = EventWaitHandle.OpenExisting(showSetupEventName);
            showSetupEvent.Set();
        }
        catch (WaitHandleCannotBeOpenedException)
        {
            // The first process is still starting or already shutting down.
        }
    }

    private static void ListenForSetupRequests(EventWaitHandle showSetupEvent)
    {
        while (true)
        {
            try
            {
                showSetupEvent.WaitOne();
                Dispatcher.UIThread.Post(() =>
                {
                    if (Application.Current is App app)
                        _ = app.ShowSessionSetupWindowAsync();
                });
            }
            catch (ObjectDisposedException)
            {
                return;
            }
        }
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}
