using Avalonia;
using Avalonia.Threading;

namespace SoulBuddy;

internal static class Program
{
    private const string SingleInstanceMutexName = @"Local\SoulBuddy.SingleInstance";
    private const string ShowSetupEventName = @"Local\SoulBuddy.ShowSetup";

    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: SingleInstanceMutexName,
            createdNew: out var createdNew);

        if (!createdNew)
        {
            if (args.Any(argument =>
                    string.Equals(argument, "--from-lua", StringComparison.OrdinalIgnoreCase)))
            {
                TrySignalExistingInstance();
            }

            return;
        }

        using var showSetupEvent = new EventWaitHandle(
            initialState: false,
            mode: EventResetMode.AutoReset,
            name: ShowSetupEventName);

        var listenerThread = new Thread(() => ListenForSetupRequests(showSetupEvent))
        {
            IsBackground = true,
            Name = "SoulBuddy setup request listener"
        };
        listenerThread.Start();

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    private static void TrySignalExistingInstance()
    {
        try
        {
            using var showSetupEvent = EventWaitHandle.OpenExisting(ShowSetupEventName);
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
