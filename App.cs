using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Themes.Fluent;
using SoulBuddy.Models;
using SoulBuddy.Services;
using SoulBuddy.Views;

namespace SoulBuddy;

public sealed class App : Application
{
    private SoulBuddyRuntime? _headlessRuntime;

    public override void Initialize()
    {
        Styles.Add(new FluentTheme());
    }

    public override void OnFrameworkInitializationCompleted()
    {
        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            var launchedFromLua = desktop.Args?.Any(argument =>
                string.Equals(argument, "--from-lua", StringComparison.OrdinalIgnoreCase)) == true;

            desktop.MainWindow = new SessionSetupWindow(launchedFromLua);
            desktop.Exit += OnDesktopExit;
        }

        base.OnFrameworkInitializationCompleted();
    }

    public async Task StartHeadlessAsync(SessionContext context)
    {
        if (_headlessRuntime is not null)
            return;

        SoullockeLaunchSettings.Configure(
            context.SoullockeLink,
            context.SoullockePassword,
            context.LocalPlayer.DisplayName);

        _headlessRuntime = await SoulBuddyRuntime.CreateAsync();
        _headlessRuntime.Start();

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        Console.WriteLine(
            $"SoulBuddy läuft ohne Hauptfenster für {context.LocalPlayer.DisplayName}. " +
            "Sync, Collector und Overlays bleiben aktiv.");
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs eventArgs)
    {
        if (_headlessRuntime is null)
            return;

        try
        {
            _headlessRuntime.DisposeAsync().AsTask().GetAwaiter().GetResult();
        }
        catch
        {
            // Process shutdown must not be blocked by cleanup errors.
        }
        finally
        {
            _headlessRuntime = null;
        }
    }
}
