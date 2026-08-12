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
            desktop.MainWindow = new SessionSetupWindow();
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
    }

    public async Task ShowSessionSetupWindowAsync()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        var existingSetup = desktop.Windows
            .OfType<SessionSetupWindow>()
            .FirstOrDefault();
        if (existingSetup is not null)
        {
            desktop.MainWindow = existingSetup;
            existingSetup.ShowInTaskbar = true;
            if (!existingSetup.IsVisible)
                existingSetup.Show();
            existingSetup.Activate();
            return;
        }

        // Keep the process alive while the previous run is being torn down and the
        // setup window becomes the new main window.
        desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;

        var setupWindow = new SessionSetupWindow();
        desktop.MainWindow = setupWindow;
        setupWindow.Show();

        if (_headlessRuntime is not null)
        {
            await _headlessRuntime.DisposeAsync();
            _headlessRuntime = null;
        }

        foreach (var window in desktop.Windows.ToArray())
        {
            if (!ReferenceEquals(window, setupWindow))
                window.Close();
        }

        desktop.ShutdownMode = ShutdownMode.OnLastWindowClose;
        setupWindow.Activate();
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
