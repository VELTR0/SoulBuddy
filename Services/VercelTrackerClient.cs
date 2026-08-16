using System.Net.Http.Headers;
using SoulBuddy.Models;

namespace SoulBuddy.Services;

/// <summary>
/// Provider boundary for soullocke.vercel.app. The upstream tracker uses Firebase
/// Realtime Database and PokeAPI-style English location names. This wrapper keeps
/// partner reads explicitly non-cached and converts those location names to the
/// canonical names SoulBuddy already uses for its Gen-4 collector so links can be
/// refreshed while SoulBuddy is running.
/// </summary>
public sealed class VercelTrackerClient : ITrackerClient
{
    private readonly VercelSoullockeClient _inner;

    public VercelTrackerClient(HttpClient httpClient, AppConfig config)
    {
        // The website itself subscribes to Firebase's realtime value events. SoulBuddy
        // polls instead, so every poll must reach Firebase rather than reusing a stale
        // intermediary response from the initial run load.
        httpClient.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
        {
            NoCache = true,
            NoStore = true,
            MaxAge = TimeSpan.Zero
        };
        if (!httpClient.DefaultRequestHeaders.Pragma.Any(value =>
                string.Equals(value.Name, "no-cache", StringComparison.OrdinalIgnoreCase)))
        {
            httpClient.DefaultRequestHeaders.Pragma.Add(new NameValueHeaderValue("no-cache"));
        }

        _inner = new VercelSoullockeClient(httpClient, config);
    }

    public string? PartnerPlayerName => _inner.PartnerPlayerName;
    public string SessionGameName => _inner.SessionGameName;
    public bool IsSynchronizationHealthy => _inner.IsSynchronizationHealthy;

    public async Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken)
    {
        var run = await _inner.LoadRunAsync(cancellationToken);
        NormalizeRunLocations(run);
        return run;
    }

    public async Task<SoullockeRun?> LoadPartnerRunAsync(CancellationToken cancellationToken)
    {
        var run = await _inner.LoadPartnerRunAsync(cancellationToken);
        if (run is not null)
            NormalizeRunLocations(run);
        return run;
    }

    public Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken) =>
        _inner.SaveRunAsync(encounters, cancellationToken);

    public Task<bool> MarkLinkedPartnerBroFailedAsync(
        string location,
        CancellationToken cancellationToken) =>
        _inner.MarkLinkedPartnerBroFailedAsync(location, cancellationToken);

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
