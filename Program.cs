using System.Text.Json;
using SoulBuddy.Data;
using SoulBuddy.Models;
using SoulBuddy.Services;
using SoulBuddy.Sources;

var workingDirectory = Directory.GetCurrentDirectory();
var executableDirectory = AppContext.BaseDirectory;

var workingConfigPath = Path.Combine(
    workingDirectory,
    "appsettings.json");

var executableConfigPath = Path.Combine(
    executableDirectory,
    "appsettings.json");

var configDirectory = File.Exists(workingConfigPath)
    ? workingDirectory
    : executableDirectory;

var defaultConfigPath = Path.Combine(
    configDirectory,
    "appsettings.json");

var localConfigPath = Path.Combine(
    configDirectory,
    "appsettings.local.json");

if (!File.Exists(defaultConfigPath))
{
    Console.WriteLine(
        $"appsettings.json wurde nicht gefunden: {defaultConfigPath}");

    return;
}

var jsonOptions = new JsonSerializerOptions
{
    PropertyNameCaseInsensitive = true
};

var defaultConfigJson =
    await File.ReadAllTextAsync(defaultConfigPath);

var config = JsonSerializer.Deserialize<AppConfig>(
    defaultConfigJson,
    jsonOptions);

if (config is null)
{
    Console.WriteLine(
        "appsettings.json ist ungültig.");

    return;
}

if (File.Exists(localConfigPath))
{
    var localConfigJson =
        await File.ReadAllTextAsync(localConfigPath);

    var localConfig = JsonSerializer.Deserialize<AppConfig>(
        localConfigJson,
        jsonOptions);

    if (localConfig is null)
    {
        Console.WriteLine(
            "appsettings.local.json ist ungültig.");

        return;
    }

    config = localConfig;

    Console.WriteLine(
        "Lokale Konfiguration wurde geladen.");
}
else
{
    Console.WriteLine(
        "Keine appsettings.local.json gefunden. " +
        "Die Standardkonfiguration wird verwendet.");
}

var partyJsonPath = Path.IsPathRooted(config.PartyJsonPath)
    ? config.PartyJsonPath
    : Path.GetFullPath(
        Path.Combine(
            configDirectory,
            config.PartyJsonPath));

var runtimeDirectory =
    Path.GetDirectoryName(partyJsonPath);

if (string.IsNullOrWhiteSpace(runtimeDirectory))
{
    Console.WriteLine(
        "Der Runtime-Ordner konnte nicht bestimmt werden.");

    return;
}

Directory.CreateDirectory(runtimeDirectory);

var eventFilePath = Path.Combine(
    runtimeDirectory,
    "emulator-events.jsonl");

var databasePath = Path.Combine(
    configDirectory,
    "soulbuddy.db");

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(15)
};

var snapshotPartySource =
    new JsonPartySource(partyJsonPath);

var livePartySource =
    new LivePartySource(snapshotPartySource);

IPartySource partySource = livePartySource;

var soullockeClient =
    new SoullockeClient(
        httpClient,
        config);

var knownPokemonStore =
    new KnownPokemonStore(databasePath);

var locationMapper =
    new LocationMapper();

var syncService =
    new SyncService(
        partySource,
        knownPokemonStore,
        soullockeClient,
        locationMapper,
        config);

var collectorEventSource =
    new JsonLineCollectorEventSource(
        eventFilePath,
        livePartySource);

using var cancellationSource =
    new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};

try
{
    await Task.WhenAll(
        syncService.RunAsync(
            cancellationSource.Token),
        collectorEventSource.RunAsync(
            cancellationSource.Token));
}
catch (OperationCanceledException)
    when (cancellationSource.IsCancellationRequested)
{
    Console.WriteLine();
    Console.WriteLine(
        "SoulBuddy wurde beendet.");
}
