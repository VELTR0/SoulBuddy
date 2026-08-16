using SoulBuddy.Models;

namespace SoulBuddy.Services;

/// <summary>
/// Provider boundary for soullocke.vercel.app. The upstream tracker uses Firebase
/// Realtime Database and PokeAPI-style English location names. This wrapper resolves
/// the database URL used by the deployed website and converts partner location names
/// to SoulBuddy's canonical Gen-4 names while leaving the writable local run untouched.
/// </summary>
public sealed class VercelTrackerClient : ITrackerClient
{
    private const string DatabaseOverrideVariable = "SOULBUDDY_VERCEL_SOULLOCKE_DATABASE_URL";
    private const string LegacyDefaultDatabaseUrl = "https://soullocke-f7500.firebaseio.com";

    private readonly HttpClient _httpClient;
    private readonly VercelSoullockeClient _inner;
    private readonly SemaphoreSlim _endpointLock = new(1, 1);
    private bool _endpointResolved;

    public VercelTrackerClient(HttpClient httpClient, AppConfig config)
    {
        _httpClient = httpClient;
        _inner = new VercelSoullockeClient(httpClient, config);
    }

    public string? PartnerPlayerName => _inner.PartnerPlayerName;
    public string SessionGameName => _inner.SessionGameName;
    public bool IsSynchronizationHealthy => _inner.IsSynchronizationHealthy;

    public async Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken)
    {
        await EnsureDatabaseEndpointAsync(cancellationToken);
        return await _inner.LoadRunAsync(cancellationToken);
    }

    public async Task<SoullockeRun?> LoadPartnerRunAsync(CancellationToken cancellationToken)
    {
        await EnsureDatabaseEndpointAsync(cancellationToken);
        var run = await _inner.LoadPartnerRunAsync(cancellationToken);
        if (run is not null)
            NormalizeRunLocations(run);
        return run;
    }

    public async Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken)
    {
        await EnsureDatabaseEndpointAsync(cancellationToken);
        await _inner.SaveRunAsync(encounters, cancellationToken);
    }

    public async Task<bool> MarkLinkedPartnerBroFailedAsync(
        string location,
        CancellationToken cancellationToken)
    {
        await EnsureDatabaseEndpointAsync(cancellationToken);
        return await _inner.MarkLinkedPartnerBroFailedAsync(location, cancellationToken);
    }

    private async Task EnsureDatabaseEndpointAsync(CancellationToken cancellationToken)
    {
        if (_endpointResolved)
            return;

        await _endpointLock.WaitAsync(cancellationToken);
        try
        {
            if (_endpointResolved)
                return;

            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(DatabaseOverrideVariable)))
            {
                var deployedUrl = await VercelDatabaseUrlResolver.TryResolveAsync(
                    _httpClient,
                    cancellationToken);

                Environment.SetEnvironmentVariable(
                    DatabaseOverrideVariable,
                    string.IsNullOrWhiteSpace(deployedUrl)
                        ? LegacyDefaultDatabaseUrl
                        : deployedUrl.TrimEnd('/'));
            }

            _endpointResolved = true;
        }
        finally
        {
            _endpointLock.Release();
        }
    }

    private static void NormalizeRunLocations(SoullockeRun run)
    {
        foreach (var pair in run.Encounters.ToArray())
        {
            var canonical = CanonicalLocation(pair.Key);
            if (string.Equals(pair.Key, canonical, StringComparison.Ordinal))
                continue;

            run.Encounters.Remove(pair.Key);
            if (!run.Encounters.TryGetValue(canonical, out var existing) || existing.Pokemon <= 0)
                run.Encounters[canonical] = pair.Value;
        }
    }

    private static string CanonicalLocation(string value)
    {
        var trimmed = value.Trim();
        if (trimmed.StartsWith("Route ", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        var normalized = new string(trimmed
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

        return normalized switch
        {
            "newbarktown" or "starter" => "Starter",
            "cherrygrovecity" => "Rosalia City",
            "violetcity" => "Viola City",
            "azaleatown" or "azaleacity" => "Azalea City",
            "cianwoodcity" => "Anemonia City",
            "goldenrodcity" => "Dukatia City",
            "olivinecity" => "Oliviana City",
            "ecruteakcity" => "Teak City",
            "mahoganytown" or "mahagoniatown" => "Mahagonia City",
            "lakeofrage" => "See des Zorns",
            "blackthorncity" => "Ebenholz City",
            "mtsilver" => "Silberberg",
            "pallettown" => "Alabastia",
            "viridiancity" => "Vertania City",
            "pewtercity" => "Marmoria City",
            "ceruleancity" => "Azuria City",
            "lavendertown" => "Lavandia",
            "vermilioncity" => "Orania City",
            "celadoncity" => "Prismania City",
            "fuchsiacity" => "Fuchsania City",
            "cinnabarisland" => "Zinnoberinsel",
            "indigoplateau" => "Indigo-Plateau",
            "saffroncity" => "Saffronia City",
            "diglettscave" or "diglettcave" => "Digdas Höhle",
            "mtmoon" => "Mondberg",
            "ceruleancave" => "Azuria-Höhle",
            "rocktunnel" => "Felstunnel",
            "powerplant" => "Kraftwerk",
            "safarizone" => "Safari-Zone",
            "seafoamislands" => "Seeschauminseln",
            "belltower" => "Glockenturm",
            "burnedtower" => "Ruinen von Teak City",
            "nationalpark" => "Nationalpark",
            "radiotower" => "Radioturm",
            "ruinsofalph" => "Alph-Ruinen",
            "unioncave" => "Einheitshöhle",
            "slowpokewell" => "Flegmon-Brunnen",
            "olivinelighthouse" or "lighthouse" => "Leuchtturm",
            "teamrockethq" or "teamrocketheadquarters" => "Rocket-Versteck",
            "ilexforest" => "Steineichenwald",
            "goldenrodtunnel" => "Dukatia-Tunnel",
            "mtmortar" => "Kesselberg",
            "icepath" => "Eispfad",
            "whirlislands" => "Strudelinseln",
            "mtsilvercave" => "Silberberghöhle",
            "darkcave" or "finsterhöhle" => "Dunkelhöhle",
            "sprouttower" => "Knofensaturm",
            "victoryroad" => "Siegesstraße (Kanto)",
            "dragonsden" => "Drachenhöhle",
            "tohjofalls" => "Tohjo-Fälle",
            "viridianforest" => "Vertania-Wald",
            "pokeathlondome" or "pokeathlon" => "Pokéathlon-Hallen",
            "ssaqua" => "M.S. Aqua",
            "safarizonegate" => "Safari-Zonen-Eingang",
            "cliffcave" => "Felsenhöhle",
            "battlefrontieraccess" => "Zugang zur Kampfzone",
            "bellchimetrail" => "Glockenklangpfad",
            "sinjohruins" => "Sinjoh-Ruinen",
            "cliffedgegate" => "Felsklippentor",
            _ => trimmed
        };
    }
}
