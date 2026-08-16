using System.Text.Json;
using SoulBuddy.Data;
using SoulBuddy.Models;
using SoulBuddy.Sources;

namespace SoulBuddy.Services;

public sealed class SoulBuddyRuntime : IAsyncDisposable
{
    private readonly HttpClient _httpClient;
    private readonly CancellationTokenSource _cancellationSource = new();
    private Task? _backgroundTask;

    private SoulBuddyRuntime(
        AppConfig config,
        string configDirectory,
        string partyJsonPath,
        string eventFilePath,
        string databasePath,
        HttpClient httpClient,
        LivePartySource livePartySource,
        PlayerLiveStateSource playerLiveStateSource,
        NuzlockeRuleEventSource nuzlockeRuleEventSource,
        KnownPokemonStore knownPokemonStore,
        SyncService syncService,
        JsonLineCollectorEventSource collectorEventSource)
    {
        Config = config;
        ConfigDirectory = configDirectory;
        PartyJsonPath = partyJsonPath;
        EventFilePath = eventFilePath;
        DatabasePath = databasePath;
        _httpClient = httpClient;
        LivePartySource = livePartySource;
        PlayerLiveStateSource = playerLiveStateSource;
        NuzlockeRuleEventSource = nuzlockeRuleEventSource;
        KnownPokemonStore = knownPokemonStore;
        SyncService = syncService;
        CollectorEventSource = collectorEventSource;
    }

    public AppConfig Config { get; }
    public string ConfigDirectory { get; }
    public string PartyJsonPath { get; }
    public string EventFilePath { get; }
    public string DatabasePath { get; }
    public LivePartySource LivePartySource { get; }
    public PlayerLiveStateSource PlayerLiveStateSource { get; }
    public NuzlockeRuleEventSource NuzlockeRuleEventSource { get; }
    public KnownPokemonStore KnownPokemonStore { get; }
    public SyncService SyncService { get; }
    public JsonLineCollectorEventSource CollectorEventSource { get; }

    public bool IsRunning =>
        _backgroundTask is not null &&
        !_backgroundTask.IsCompleted;

    public static async Task<SoulBuddyRuntime> CreateAsync(
        CancellationToken cancellationToken = default)
    {
        var configDirectory = FindConfigDirectory();
        var defaultConfigPath = Path.Combine(configDirectory, "appsettings.json");
        var localConfigPath = Path.Combine(configDirectory, "appsettings.local.json");

        if (!File.Exists(defaultConfigPath))
        {
            throw new FileNotFoundException(
                "appsettings.json wurde nicht gefunden.",
                defaultConfigPath);
        }

        var jsonOptions = new JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true
        };

        var defaultConfigJson = await File.ReadAllTextAsync(
            defaultConfigPath,
            cancellationToken);

        var config = JsonSerializer.Deserialize<AppConfig>(defaultConfigJson, jsonOptions)
            ?? throw new InvalidOperationException("appsettings.json ist ungültig.");

        if (File.Exists(localConfigPath))
        {
            var localConfigJson = await File.ReadAllTextAsync(localConfigPath, cancellationToken);
            config = JsonSerializer.Deserialize<AppConfig>(localConfigJson, jsonOptions)
                ?? throw new InvalidOperationException("appsettings.local.json ist ungültig.");
        }

        config = SoullockeLaunchSettings.Apply(config);

        var partyJsonPath = Path.IsPathRooted(config.PartyJsonPath)
            ? Path.GetFullPath(config.PartyJsonPath)
            : Path.GetFullPath(Path.Combine(configDirectory, config.PartyJsonPath));
        partyJsonPath = LuaLaunchContext.ScopePath(partyJsonPath);

        var runtimeDirectory = Path.GetDirectoryName(partyJsonPath);
        if (string.IsNullOrWhiteSpace(runtimeDirectory))
        {
            throw new InvalidOperationException(
                "Der Runtime-Ordner konnte nicht bestimmt werden.");
        }

        Directory.CreateDirectory(runtimeDirectory);

        var eventFilePath = LuaLaunchContext.ScopePath(
            Path.Combine(runtimeDirectory, "emulator-events.jsonl"));
        var overlayEventFilePath = LuaLaunchContext.ScopePath(
            Path.Combine(runtimeDirectory, "overlay-events.jsonl"));
        var databasePath = BuildDatabasePath(configDirectory, config);
        var httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
        var snapshotPartySource = new JsonPartySource(partyJsonPath);
        var livePartySource = new LivePartySource(
            snapshotPartySource,
            initializeFromSnapshot: !config.SoullockeEnabled);

        var playerLiveStateSource = new PlayerLiveStateSource();
        var knownPokemonStore = new KnownPokemonStore(databasePath);
        var locationMapper = new LocationMapper();
        var nuzlockeRuleEventSource = new NuzlockeRuleEventSource(locationMapper);
        var overlayMessageWriter = new OverlayMessageWriter(overlayEventFilePath);
        nuzlockeRuleEventSource.EventOccurred += (_, ruleEvent) =>
            overlayMessageWriter.Write(ruleEvent);

        ITrackerClient soullockeClient = SoullockeLaunchSettings.Provider switch
        {
            TrackerProviderKind.VercelSoullocke =>
                new VercelSoullockeTrackerClient(httpClient, config),
            _ => new LegacySoullockeTrackerClient(httpClient, config)
        };

        var syncService = new SyncService(
            livePartySource,
            knownPokemonStore,
            soullockeClient,
            locationMapper,
            nuzlockeRuleEventSource,
            config);
        var collectorEventSource = new JsonLineCollectorEventSource(
            eventFilePath,
            livePartySource,
            playerLiveStateSource,
            nuzlockeRuleEventSource);

        // Do not initialize the tracker here. The collector must be able to start even
        // while the remote service is unavailable or still connecting. SyncService
        // performs its own initialization in the background after Start().
        return new SoulBuddyRuntime(
            config,
            configDirectory,
            partyJsonPath,
            eventFilePath,
            databasePath,
            httpClient,
            livePartySource,
            playerLiveStateSource,
            nuzlockeRuleEventSource,
            knownPokemonStore,
            syncService,
            collectorEventSource);
    }

    private static string BuildDatabasePath(string configDirectory, AppConfig config)
    {
        if (!config.SoullockeEnabled || string.IsNullOrWhiteSpace(config.SessionId))
            return Path.Combine(configDirectory, "soulbuddy.db");

        // The configured tracker is the persistent source of truth. Each SoulBuddy
        // runtime gets a unique temporary Pokémon database so no encounter/team state
        // can leak from a previous launch and overlapping shutdown/startup cannot lock
        // the next session out of its database.
        var session = SanitizePathComponent(config.SessionId);
        var player = SanitizePathComponent(config.PlayerName);
        var run = Math.Max(1, config.RunNumber);
        var runtimeId = $"{Environment.ProcessId}-{Guid.NewGuid():N}";

        return Path.Combine(
            Path.GetTempPath(),
            "SoulBuddy",
            "pokemon",
            session,
            player,
            $"run-{run}",
            $"runtime-{runtimeId}.db");
    }

    private static void TryDeleteSoullockePokemonDatabase(string databasePath)
    {
        foreach (var path in new[]
                 {
                     databasePath,
                     databasePath + "-wal",
                     databasePath + "-shm"
                 })
        {
            try
            {
                File.Delete(path);
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }
    }

    private static string SanitizePathComponent(string value)
    {
        var sanitized = new string(value
            .Trim()
            .Select(character =>
                char.IsLetterOrDigit(character) || character is '-' or '_'
                    ? character
                    : '_')
            .ToArray());

        return string.IsNullOrWhiteSpace(sanitized)
            ? "unknown"
            : sanitized;
    }

    private static string FindConfigDirectory()
    {
        var searchRoots = new[]
        {
            Directory.GetCurrentDirectory(),
            AppContext.BaseDirectory
        };

        foreach (var searchRoot in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var projectDirectory = FindProjectDirectory(searchRoot);
            if (projectDirectory is not null)
                return projectDirectory;
        }

        foreach (var searchRoot in searchRoots.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var configDirectory = FindDirectoryContainingFile(searchRoot, "appsettings.json");
            if (configDirectory is not null)
                return configDirectory;
        }

        throw new FileNotFoundException(
            "Der SoulBuddy-Projektordner mit appsettings.json wurde nicht gefunden.");
    }

    private static string? FindProjectDirectory(string startPath)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        while (directory is not null)
        {
            var configPath = Path.Combine(directory.FullName, "appsettings.json");
            var collectorPath = Path.Combine(directory.FullName, "collectors", "desmume-gen4");
            if (File.Exists(configPath) && Directory.Exists(collectorPath))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    private static string? FindDirectoryContainingFile(string startPath, string fileName)
    {
        var directory = new DirectoryInfo(Path.GetFullPath(startPath));
        while (directory is not null)
        {
            if (File.Exists(Path.Combine(directory.FullName, fileName)))
                return directory.FullName;

            directory = directory.Parent;
        }

        return null;
    }

    public void Start()
    {
        if (_backgroundTask is not null)
            return;

        _backgroundTask = RunBackgroundAsync(_cancellationSource.Token);
    }

    private async Task RunBackgroundAsync(CancellationToken cancellationToken)
    {
        try
        {
            await Task.WhenAll(
                SyncService.RunAsync(cancellationToken),
                CollectorEventSource.RunAsync(cancellationToken));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
        }
    }

    public async ValueTask DisposeAsync()
    {
        _cancellationSource.Cancel();
        if (_backgroundTask is not null)
        {
            try
            {
                await _backgroundTask;
            }
            catch (OperationCanceledException)
            {
            }
        }

        _cancellationSource.Dispose();
        _httpClient.Dispose();

        if (Config.SoullockeEnabled)
            TryDeleteSoullockePokemonDatabase(DatabasePath);
    }
}
