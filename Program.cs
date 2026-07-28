using System.Text.Json;
using SoulBuddy.Data;
using SoulBuddy.Models;
using SoulBuddy.Services;
using SoulBuddy.Sources;

var baseDirectory = AppContext.BaseDirectory;
var configPath = Path.Combine(baseDirectory, "appsettings.json");

if (!File.Exists(configPath))
{
    Console.WriteLine(
        $"appsettings.json wurde nicht gefunden: {configPath}");

    return;
}

var configJson = await File.ReadAllTextAsync(configPath);

var config = JsonSerializer.Deserialize<AppConfig>(
    configJson,
    new JsonSerializerOptions
    {
        PropertyNameCaseInsensitive = true
    });

if (config is null)
{
    Console.WriteLine("appsettings.json ist ungültig.");
    return;
}

using var httpClient = new HttpClient
{
    Timeout = TimeSpan.FromSeconds(15)
};

IPartySource partySource =
    new JsonPartySource(config.PartyJsonPath);
var soullockeClient = new SoullockeClient(httpClient, config);
var knownPokemonStore = new KnownPokemonStore(
    Path.Combine(baseDirectory, "known-pokemon.json"));
var locationMapper = new LocationMapper();

var syncService = new SyncService(
    partySource,
    knownPokemonStore,
    soullockeClient,
    locationMapper,
    config);

using var cancellationSource = new CancellationTokenSource();

Console.CancelKeyPress += (_, eventArgs) =>
{
    eventArgs.Cancel = true;
    cancellationSource.Cancel();
};

await syncService.RunAsync(cancellationSource.Token);