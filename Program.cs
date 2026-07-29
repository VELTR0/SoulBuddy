using System.Text.Json;
using SoulBuddy.Data;
using SoulBuddy.Models;
using SoulBuddy.Services;
using SoulBuddy.Sources;

var baseDirectory = AppContext.BaseDirectory;

var defaultConfigPath = Path.Combine(
    baseDirectory,
    "appsettings.json");

var localConfigPath = Path.Combine(
    baseDirectory,
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
    Console.WriteLine("appsettings.json ist ungültig.");
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
            baseDirectory,
            config.PartyJsonPath));

var databasePath = Path.Combine(
    baseDirectory,
    "soulbuddy.db");

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(15)
};

IPartySource partySource =
    new JsonPartySource(partyJsonPath);

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

using var cancellationSource =
    new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};

await syncService.RunAsync(
    cancellationSource.Token);