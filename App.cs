using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Threading;
using Avalonia.Themes.Fluent;
using SoulBuddy.Models;
using SoulBuddy.Services;
using SoulBuddy.Views;

namespace SoulBuddy;

public sealed class App : Application
{
    private SoulBuddyRuntime? _headlessRuntime;
    private CancellationTokenSource? _headlessLuaStopWatchCancellation;
    private Task? _headlessLuaStopWatchTask;
    private string? _headlessLuaLaunchToken;

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

        StartHeadlessLuaStopWatch();
    }

    public async Task ShowSessionSetupWindowAsync()
    {
        if (ApplicationLifetime is not IClassicDesktopStyleApplicationLifetime desktop)
            return;

        // A new Lua launch supersedes the lifecycle of the previous headless run.
        // Cancel its stop watcher before tearing that runtime down so an old exit
        // signal cannot shut down the setup window for the new run.
        CancelHeadlessLuaStopWatch();

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

    private void StartHeadlessLuaStopWatch()
    {
        CancelHeadlessLuaStopWatch();

        if (_headlessRuntime is null)
            return;

        var runtimeDirectory = Path.GetDirectoryName(_headlessRuntime.EventFilePath);
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
            return;

        var requestFilePath = Path.Combine(runtimeDirectory, "soulbuddy-request.txt");
        var launchToken = TryReadSignalToken(requestFilePath);

        // A manually launched headless SoulBuddy has no active Lua request and must
        // stay alive until the user closes it by another explicit action.
        if (string.IsNullOrWhiteSpace(launchToken))
            return;

        var stopFilePath = Path.Combine(runtimeDirectory, "soulbuddy-lua-stopped.txt");
        _headlessLuaLaunchToken = launchToken;
        _headlessLuaStopWatchCancellation = new CancellationTokenSource();
        _headlessLuaStopWatchTask = WatchHeadlessLuaStopAsync(
            launchToken,
            requestFilePath,
            stopFilePath,
            _headlessLuaStopWatchCancellation.Token);
    }

    private async Task WatchHeadlessLuaStopAsync(
        string launchToken,
        string requestFilePath,
        string stopFilePath,
        CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                var stopToken = TryReadSignalToken(stopFilePath);
                if (string.Equals(stopToken, launchToken, StringComparison.Ordinal))
                {
                    // If a newer Lua start already replaced the request token, the old
                    // script's exit callback must not terminate the process that is now
                    // being reused for the new setup/run.
                    var currentRequestToken = TryReadSignalToken(requestFilePath);
                    if (!string.IsNullOrWhiteSpace(currentRequestToken) &&
                        !string.Equals(currentRequestToken, launchToken, StringComparison.Ordinal))
                    {
                        return;
                    }

                    Dispatcher.UIThread.Post(() =>
                    {
                        _ = StopHeadlessAfterLuaExitAsync(launchToken, stopFilePath);
                    });
                    return;
                }
            }
            catch
            {
                // A transient file-system error should not kill the background run.
            }

            try
            {
                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                return;
            }
        }
    }

    private async Task StopHeadlessAfterLuaExitAsync(
        string launchToken,
        string stopFilePath)
    {
        if (_headlessRuntime is null ||
            !string.Equals(_headlessLuaLaunchToken, launchToken, StringComparison.Ordinal))
        {
            return;
        }

        CancelHeadlessLuaStopWatch();

        var runtime = _headlessRuntime;
        _headlessRuntime = null;

        try
        {
            await runtime.DisposeAsync();
        }
        finally
        {
            TryDeleteMatchingSignal(stopFilePath, launchToken);

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                desktop.Shutdown();
        }
    }

    private void CancelHeadlessLuaStopWatch()
    {
        var cancellation = _headlessLuaStopWatchCancellation;
        _headlessLuaStopWatchCancellation = null;
        _headlessLuaStopWatchTask = null;
        _headlessLuaLaunchToken = null;

        if (cancellation is null)
            return;

        try
        {
            cancellation.Cancel();
        }
        catch (ObjectDisposedException)
        {
        }
        finally
        {
            cancellation.Dispose();
        }
    }

    private static string? TryReadSignalToken(string path)
    {
        try
        {
            if (!File.Exists(path))
                return null;

            using var reader = new StreamReader(path);
            var token = reader.ReadLine()?.Trim();
            return string.IsNullOrWhiteSpace(token) ? null : token;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }

    private static void TryDeleteMatchingSignal(string path, string expectedToken)
    {
        try
        {
            if (!string.Equals(
                    TryReadSignalToken(path),
                    expectedToken,
                    StringComparison.Ordinal))
            {
                return;
            }

            File.Delete(path);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    private void OnDesktopExit(object? sender, ControlledApplicationLifetimeExitEventArgs eventArgs)
    {
        CancelHeadlessLuaStopWatch();

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
