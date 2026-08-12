using Avalonia;

namespace SoulBuddy;

internal static class Program
{
    [STAThread]
    public static void Main(string[] args)
    {
        using var singleInstance = new Mutex(
            initiallyOwned: true,
            name: @"Local\SoulBuddy.SingleInstance",
            createdNew: out var createdNew);

        if (!createdNew)
            return;

        BuildAvaloniaApp()
            .StartWithClassicDesktopLifetime(args);
    }

    public static AppBuilder BuildAvaloniaApp()
    {
        return AppBuilder
            .Configure<App>()
            .UsePlatformDetect()
            .LogToTrace();
    }
}
