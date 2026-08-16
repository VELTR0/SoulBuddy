using System.Collections.Concurrent;
using System.Net;
using System.Text;
using System.Text.Json;
using SoulBuddy.Models;

namespace SoulBuddy.Services;

/// <summary>
/// Adapter for https://soullocke.vercel.app (jynnie/soullocke).
/// The tracker stores runs in Firebase Realtime Database. SoulBuddy reads the shared
/// run, keeps partner data read-only, and writes only the configured local player's
/// Pokémon plus shared timeline entries that are required for new encounters.
/// </summary>
public sealed class VercelSoullockeTrackerClient : ITrackerClient
{
    private static readonly string[] DatabaseBaseUrlCandidates =
    [
        "https://soullocke-f7500-default-rtdb.firebaseio.com",
        "https://soullocke-f7500-default-rtdb.europe-west1.firebasedatabase.app",
        "https://soullocke-f7500-default-rtdb.asia-southeast1.firebasedatabase.app"
    ];

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web)
    {
        PropertyNameCaseInsensitive = true
    };

    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly SemaphoreSlim _initializationLock = new(1, 1);
    private readonly PokemonSpeciesResolver _speciesResolver;

    private string? _databaseBaseUrl;
    private string? _localPlayerId;
    private string? _partnerPlayerId;
    private string? _partnerPlayerName;
    private string _gameName = string.Empty;
    private Dictionary<string, VercelTimelineEntry> _timeline =
        new(StringComparer.OrdinalIgnoreCase);
    private Dictionary<string, VercelPokemon> _localPokemon =
        new(StringComparer.OrdinalIgnoreCase);
    private bool _initialized;

    public VercelSoullockeTrackerClient(HttpClient httpClient, AppConfig config)
    {
        _httpClient = httpClient;
        _config = config;
        _speciesResolver = new PokemonSpeciesResolver(httpClient);
    }

    public string? PartnerPlayerName => _partnerPlayerName;

    public bool IsSynchronizationHealthy { get; private set; }

    public async Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        return await BuildRunAsync(
            _localPlayerId!,
            _localPokemon,
            cancellationToken);
    }

    public async Task<SoullockeRun?> LoadPartnerRunAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(_partnerPlayerId))
            return null;

        var remote = await LoadRemoteRunAsync(cancellationToken);
        _timeline = remote.Timeline ?? new Dictionary<string, VercelTimelineEntry>(
            StringComparer.OrdinalIgnoreCase);

        if (!remote.Players.TryGetValue(_partnerPlayerId, out var partner))
            return null;

        _partnerPlayerName = partner.Name;
        var run = await BuildRunAsync(
            partner.Id,
            partner.Pokemon,
            cancellationToken);

        SoullockePartnerCatchObserver.ObserveLoadedRuns(
            new Dictionary<string, SoullockeRun>(StringComparer.OrdinalIgnoreCase)
            {
                [partner.Id] = run
            });

        return run;
    }

    public async Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);

        var patch = new Dictionary<string, object?>();

        foreach (var pair in encounters)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var location = pair.Key.Trim();
            if (string.IsNullOrWhiteSpace(location) || pair.Value.Pokemon <= 0)
                continue;

            var origin = ResolveOrCreateOrigin(location, patch);
            _localPokemon.TryGetValue(origin, out var pokemon);

            var speciesName = await _speciesResolver.ResolveNameAsync(
                pair.Value.Pokemon,
                cancellationToken);
            var status = NormalizeStatus(pair.Value.Status);

            if (pokemon is null)
            {
                pokemon = CreatePokemon(
                    origin,
                    speciesName,
                    pair.Value.Nickname,
                    status);
                _localPokemon[origin] = pokemon;
            }
            else
            {
                ApplyPokemonChanges(
                    pokemon,
                    speciesName,
                    pair.Value.Nickname,
                    status,
                    origin);
            }

            patch[$"players/{_localPlayerId}/pokemon/{origin}"] = pokemon;
        }

        if (patch.Count == 0)
        {
            IsSynchronizationHealthy = true;
            return;
        }

        await PatchRunAsync(patch, cancellationToken);
    }

    private async Task EnsureInitializedAsync(CancellationToken cancellationToken)
    {
        if (_initialized)
            return;

        await _initializationLock.WaitAsync(cancellationToken);
        try
        {
            if (_initialized)
                return;

            if (string.IsNullOrWhiteSpace(_config.SessionId))
            {
                throw new InvalidOperationException(
                    "Der Soullocke-Link enthält keine gültige Run-ID.");
            }

            var remote = await LoadRemoteRunAsync(cancellationToken);
            _gameName = remote.Game?.Trim() ?? string.Empty;
            _timeline = remote.Timeline ?? new Dictionary<string, VercelTimelineEntry>(
                StringComparer.OrdinalIgnoreCase);

            var matchingPlayers = remote.Players.Values
                .Where(player => string.Equals(
                    player.Name?.Trim(),
                    _config.PlayerName.Trim(),
                    StringComparison.OrdinalIgnoreCase))
                .ToArray();

            if (matchingPlayers.Length == 0)
            {
                var available = string.Join(
                    ", ",
                    remote.Players.Values
                        .Select(player => player.Name)
                        .Where(name => !string.IsNullOrWhiteSpace(name)));
                throw new InvalidOperationException(
                    $"Der Spielername '{_config.PlayerName}' wurde im Soullocke-Run nicht gefunden." +
                    (string.IsNullOrWhiteSpace(available)
                        ? string.Empty
                        : $" Verfügbar: {available}."));
            }

            if (matchingPlayers.Length > 1)
            {
                throw new InvalidOperationException(
                    $"Der Spielername '{_config.PlayerName}' kommt im Soullocke-Run mehrfach vor. " +
                    "Bitte verwende dort eindeutige Spielernamen.");
            }

            var localPlayer = matchingPlayers[0];
            _localPlayerId = localPlayer.Id;
            _localPokemon = localPlayer.Pokemon ?? new Dictionary<string, VercelPokemon>(
                StringComparer.OrdinalIgnoreCase);

            _config.PlayerId = localPlayer.Id;
            _config.TeamName = localPlayer.Name;

            var partner = remote.Players.Values
                .FirstOrDefault(player => !string.Equals(
                    player.Id,
                    localPlayer.Id,
                    StringComparison.OrdinalIgnoreCase));
            _partnerPlayerId = partner?.Id;
            _partnerPlayerName = partner?.Name;

            _initialized = true;
            IsSynchronizationHealthy = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<SoullockeRun> BuildRunAsync(
        string playerId,
        IReadOnlyDictionary<string, VercelPokemon>? pokemonByOrigin,
        CancellationToken cancellationToken)
    {
        var encounters = new Dictionary<string, SoullockeEncounter>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var pair in pokemonByOrigin ?? new Dictionary<string, VercelPokemon>())
        {
            cancellationToken.ThrowIfCancellationRequested();

            var pokemon = pair.Value;
            if (pokemon is null || string.IsNullOrWhiteSpace(pokemon.Name))
                continue;

            var currentSpeciesName = GetCurrentSpeciesName(pokemon);
            var speciesId = await _speciesResolver.ResolveIdAsync(
                currentSpeciesName,
                cancellationToken);
            if (speciesId <= 0)
                continue;

            var origin = string.IsNullOrWhiteSpace(pokemon.Origin)
                ? pair.Key
                : pokemon.Origin;
            var location = _timeline.TryGetValue(origin, out var timelineEntry) &&
                           !string.IsNullOrWhiteSpace(timelineEntry.Name)
                ? timelineEntry.Name
                : origin;

            encounters[location] = new SoullockeEncounter
            {
                Pokemon = speciesId,
                Nickname = string.IsNullOrWhiteSpace(pokemon.Nickname)
                    ? null
                    : pokemon.Nickname,
                Status = GetInternalStatus(pokemon)
            };
        }

        var run = new SoullockeRun
        {
            PlayerId = playerId,
            RunNumber = Math.Max(1, _config.RunNumber),
            GameName = _gameName,
            Status = "open",
            Encounters = encounters
        };

        return run;
    }

    private string ResolveOrCreateOrigin(
        string location,
        Dictionary<string, object?> patch)
    {
        var normalized = NormalizeLocation(location);
        var existing = _timeline.Values
            .OrderBy(entry => entry.Index)
            .FirstOrDefault(entry =>
                NormalizeLocation(entry.Name) == normalized);
        if (existing is not null && !string.IsNullOrWhiteSpace(existing.Key))
            return existing.Key;

        var origin = Guid.NewGuid().ToString();
        var nextIndex = _timeline.Count == 0
            ? 0
            : _timeline.Values.Max(entry => entry.Index) + 1;
        var timelineEntry = new VercelTimelineEntry
        {
            Key = origin,
            Index = nextIndex,
            Name = location
        };

        _timeline[origin] = timelineEntry;
        patch[$"timeline/{origin}"] = timelineEntry;
        return origin;
    }

    private VercelPokemon CreatePokemon(
        string origin,
        string speciesName,
        string? nickname,
        string status)
    {
        var isMissed = status == "notcaught";
        var pokemon = new VercelPokemon
        {
            PlayerId = _localPlayerId!,
            Origin = origin,
            Name = speciesName,
            Nickname = string.IsNullOrWhiteSpace(nickname) ? speciesName : nickname,
            Location = isMissed ? "grave" : ToVercelLocation(status),
            Events = new Dictionary<string, VercelPokemonEvent>(StringComparer.Ordinal)
            {
                ["0"] = new()
                {
                    Index = "0",
                    Type = isMissed ? 1 : 0,
                    Location = origin
                }
            }
        };

        if (status is "fainted" or "brofailed")
        {
            pokemon.Location = "grave";
            AppendEvent(
                pokemon,
                status == "brofailed" ? 4 : 3,
                origin,
                "grave");
        }

        return pokemon;
    }

    private static void ApplyPokemonChanges(
        VercelPokemon pokemon,
        string speciesName,
        string? nickname,
        string status,
        string origin)
    {
        pokemon.Events ??= new Dictionary<string, VercelPokemonEvent>(StringComparer.Ordinal);
        pokemon.Origin = origin;

        if (string.IsNullOrWhiteSpace(pokemon.Name))
        {
            pokemon.Name = speciesName;
        }
        else if (!string.Equals(
                     GetCurrentSpeciesName(pokemon),
                     speciesName,
                     StringComparison.OrdinalIgnoreCase))
        {
            AppendEvent(
                pokemon,
                5,
                origin,
                evolution: speciesName);
        }

        pokemon.Nickname = string.IsNullOrWhiteSpace(nickname)
            ? pokemon.Nickname ?? speciesName
            : nickname;

        if (StatusMatchesLocation(status, pokemon.Location))
            return;

        switch (status)
        {
            case "alive":
                pokemon.Location = "team";
                AppendEvent(pokemon, 2, origin, "team");
                break;
            case "boxed":
                pokemon.Location = "box";
                AppendEvent(pokemon, 2, origin, "box");
                break;
            case "notcaught":
                pokemon.Location = "grave";
                AppendEvent(pokemon, 1, origin, "grave");
                break;
            case "brofailed":
                pokemon.Location = "grave";
                AppendEvent(pokemon, 4, origin, "grave");
                break;
            case "fainted":
                pokemon.Location = "grave";
                AppendEvent(pokemon, 3, origin, "grave");
                break;
        }
    }

    private static string GetCurrentSpeciesName(VercelPokemon pokemon)
    {
        var evolved = (pokemon.Events ?? new Dictionary<string, VercelPokemonEvent>())
            .Where(pair => pair.Value.Type == 5 &&
                           !string.IsNullOrWhiteSpace(pair.Value.Details?.Evolution))
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .Select(pair => pair.Value.Details!.Evolution!)
            .LastOrDefault();

        return string.IsNullOrWhiteSpace(evolved)
            ? pokemon.Name
            : evolved;
    }

    private static string GetInternalStatus(VercelPokemon pokemon)
    {
        var events = pokemon.Events?.Values ?? [];
        if (events.Any(evt => evt.Type == 1))
            return "notcaught";
        if (events.Any(evt => evt.Type is 4 or 6))
            return "brofailed";

        return (pokemon.Location ?? string.Empty).Trim().ToLowerInvariant() switch
        {
            "box" or "daycare" => "boxed",
            "grave" => events.Any(evt => evt.Type == 3) ? "fainted" : "fainted",
            _ => "alive"
        };
    }

    private static bool StatusMatchesLocation(string status, string? location)
    {
        var current = (location ?? string.Empty).Trim().ToLowerInvariant();
        return status switch
        {
            "alive" => current == "team",
            "boxed" => current is "box" or "daycare",
            "notcaught" or "brofailed" or "fainted" => current == "grave",
            _ => false
        };
    }

    private static string ToVercelLocation(string status) => status switch
    {
        "boxed" => "box",
        "fainted" or "brofailed" or "notcaught" => "grave",
        _ => "team"
    };

    private static string NormalizeStatus(string? status) =>
        (status ?? "alive").Trim().ToLowerInvariant() switch
        {
            "box" or "boxed" => "boxed",
            "notcaught" or "not-caught" or "not-catched" => "notcaught",
            "brofailed" or "bro-failed" => "brofailed",
            "fainted" => "fainted",
            _ => "alive"
        };

    private static string NormalizeLocation(string? value) =>
        new((value ?? string.Empty)
            .Trim()
            .ToLowerInvariant()
            .Where(char.IsLetterOrDigit)
            .ToArray());

    private static void AppendEvent(
        VercelPokemon pokemon,
        int type,
        string location,
        string? pokemonLocation = null,
        string? evolution = null)
    {
        pokemon.Events ??= new Dictionary<string, VercelPokemonEvent>(StringComparer.Ordinal);
        var key = FirebasePushId.NewId();
        pokemon.Events[key] = new VercelPokemonEvent
        {
            Index = key,
            Type = type,
            Location = location,
            Details = pokemonLocation is null && evolution is null
                ? null
                : new VercelEventDetails
                {
                    Location = pokemonLocation,
                    Evolution = evolution
                }
        };
    }

    private async Task<VercelRun> LoadRemoteRunAsync(CancellationToken cancellationToken)
    {
        if (!string.IsNullOrWhiteSpace(_databaseBaseUrl))
        {
            try
            {
                return await GetRunFromDatabaseAsync(_databaseBaseUrl, cancellationToken);
            }
            catch
            {
                IsSynchronizationHealthy = false;
                throw;
            }
        }

        var failures = new List<string>();
        foreach (var candidate in DatabaseBaseUrlCandidates)
        {
            try
            {
                var run = await GetRunFromDatabaseAsync(candidate, cancellationToken);
                _databaseBaseUrl = candidate;
                IsSynchronizationHealthy = true;
                return run;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                failures.Add($"{candidate}: {ex.Message}");
            }
        }

        IsSynchronizationHealthy = false;
        throw new InvalidOperationException(
            "Der Run auf soullocke.vercel.app konnte nicht aus Firebase geladen werden. " +
            string.Join(" | ", failures));
    }

    private async Task<VercelRun> GetRunFromDatabaseAsync(
        string databaseBaseUrl,
        CancellationToken cancellationToken)
    {
        var url = BuildRunUrl(databaseBaseUrl);
        using var response = await _httpClient.GetAsync(url, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"Firebase antwortete mit {(int)response.StatusCode} {response.StatusCode}: {body}");
        }

        if (string.Equals(body.Trim(), "null", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Der Soullocke-Run '{_config.SessionId}' wurde nicht gefunden.");
        }

        var run = JsonSerializer.Deserialize<VercelRun>(body, JsonOptions)
            ?? throw new InvalidOperationException("Firebase hat ungültige Run-Daten zurückgegeben.");
        run.Players ??= new Dictionary<string, VercelPlayer>(StringComparer.OrdinalIgnoreCase);
        run.Timeline ??= new Dictionary<string, VercelTimelineEntry>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in run.Players)
        {
            pair.Value.Id = string.IsNullOrWhiteSpace(pair.Value.Id) ? pair.Key : pair.Value.Id;
            pair.Value.Pokemon ??= new Dictionary<string, VercelPokemon>(StringComparer.OrdinalIgnoreCase);
        }

        IsSynchronizationHealthy = true;
        return run;
    }

    private async Task PatchRunAsync(
        Dictionary<string, object?> patch,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(_databaseBaseUrl))
            throw new InvalidOperationException("Die Firebase-Datenbank wurde noch nicht initialisiert.");

        var json = JsonSerializer.Serialize(patch, JsonOptions);
        using var request = new HttpRequestMessage(HttpMethod.Patch, BuildRunUrl(_databaseBaseUrl))
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        using var response = await _httpClient.SendAsync(request, cancellationToken);
        var body = await response.Content.ReadAsStringAsync(cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            IsSynchronizationHealthy = false;
            throw new HttpRequestException(
                $"Soullocke konnte nicht in Firebase gespeichert werden: " +
                $"{(int)response.StatusCode} {response.StatusCode} {body}");
        }

        IsSynchronizationHealthy = true;
    }

    private string BuildRunUrl(string databaseBaseUrl) =>
        $"{databaseBaseUrl.TrimEnd('/')}/{Uri.EscapeDataString(_config.SessionId)}.json";

    private sealed class PokemonSpeciesResolver
    {
        private readonly HttpClient _httpClient;
        private readonly ConcurrentDictionary<string, int> _idsByName =
            new(StringComparer.OrdinalIgnoreCase);
        private readonly ConcurrentDictionary<int, string> _namesById = new();

        public PokemonSpeciesResolver(HttpClient httpClient)
        {
            _httpClient = httpClient;
        }

        public async Task<int> ResolveIdAsync(string name, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(name))
                return 0;
            if (_idsByName.TryGetValue(name, out var cached))
                return cached;

            var normalized = name.Trim().ToLowerInvariant().Replace(' ', '-');
            using var response = await _httpClient.GetAsync(
                $"https://pokeapi.co/api/v2/pokemon/{Uri.EscapeDataString(normalized)}",
                cancellationToken);
            if (response.StatusCode == HttpStatusCode.NotFound)
                return 0;
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            var id = document.RootElement.GetProperty("id").GetInt32();
            var canonicalName = document.RootElement.GetProperty("name").GetString() ?? normalized;
            _idsByName[name] = id;
            _idsByName[canonicalName] = id;
            _namesById[id] = canonicalName;
            return id;
        }

        public async Task<string> ResolveNameAsync(int id, CancellationToken cancellationToken)
        {
            if (_namesById.TryGetValue(id, out var cached))
                return cached;

            using var response = await _httpClient.GetAsync(
                $"https://pokeapi.co/api/v2/pokemon/{id}",
                cancellationToken);
            response.EnsureSuccessStatusCode();

            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                stream,
                cancellationToken: cancellationToken);
            var name = document.RootElement.GetProperty("name").GetString()
                ?? throw new InvalidOperationException($"Pokémon #{id} hat keinen Namen in PokéAPI.");
            _namesById[id] = name;
            _idsByName[name] = id;
            return name;
        }
    }

    private static class FirebasePushId
    {
        private const string Alphabet = "-0123456789ABCDEFGHIJKLMNOPQRSTUVWXYZ_abcdefghijklmnopqrstuvwxyz";
        private static readonly object Sync = new();
        private static readonly Random Random = new();
        private static readonly int[] LastRandom = new int[12];
        private static long _lastTimestamp;

        public static string NewId()
        {
            lock (Sync)
            {
                var timestamp = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
                var duplicateTime = timestamp == _lastTimestamp;
                _lastTimestamp = timestamp;

                Span<char> timeChars = stackalloc char[8];
                var value = timestamp;
                for (var i = 7; i >= 0; i--)
                {
                    timeChars[i] = Alphabet[(int)(value % 64)];
                    value /= 64;
                }

                if (!duplicateTime)
                {
                    for (var i = 0; i < LastRandom.Length; i++)
                        LastRandom[i] = Random.Next(64);
                }
                else
                {
                    var index = LastRandom.Length - 1;
                    while (index >= 0 && LastRandom[index] == 63)
                    {
                        LastRandom[index] = 0;
                        index--;
                    }
                    if (index >= 0)
                        LastRandom[index]++;
                }

                var builder = new StringBuilder(20);
                builder.Append(timeChars);
                foreach (var randomValue in LastRandom)
                    builder.Append(Alphabet[randomValue]);
                return builder.ToString();
            }
        }
    }

    private sealed class VercelRun
    {
        public string Id { get; set; } = string.Empty;
        public string Game { get; set; } = string.Empty;
        public string Region { get; set; } = string.Empty;
        public string? Password { get; set; }
        public Dictionary<string, VercelTimelineEntry> Timeline { get; set; } = [];
        public Dictionary<string, VercelPlayer> Players { get; set; } = [];
    }

    private sealed class VercelPlayer
    {
        public string Id { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public Dictionary<string, VercelPokemon> Pokemon { get; set; } = [];
    }

    private sealed class VercelPokemon
    {
        public string PlayerId { get; set; } = string.Empty;
        public string Origin { get; set; } = string.Empty;
        public string Name { get; set; } = string.Empty;
        public string? Nickname { get; set; }
        public Dictionary<string, VercelPokemonEvent> Events { get; set; } = [];
        public string Location { get; set; } = "box";
        public bool? Shiny { get; set; }
    }

    private sealed class VercelPokemonEvent
    {
        public string Index { get; set; } = string.Empty;
        public int Type { get; set; }
        public string Location { get; set; } = string.Empty;
        public VercelEventDetails? Details { get; set; }
    }

    private sealed class VercelEventDetails
    {
        public string? Location { get; set; }
        public string? Evolution { get; set; }
    }

    private sealed class VercelTimelineEntry
    {
        public string Key { get; set; } = string.Empty;
        public int Index { get; set; }
        public string Name { get; set; } = string.Empty;
        public string? Notes { get; set; }
    }
}
