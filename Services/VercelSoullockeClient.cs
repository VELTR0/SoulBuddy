using System.Net.Http.Json;
using System.Text.Json;
using System.Text.Json.Serialization;
using SoulBuddy.Models;

namespace SoulBuddy.Services;

/// <summary>
/// Adapter for https://soullocke.vercel.app. The tracker stores each run as one
/// top-level Firebase Realtime Database node. SoulBuddy reads the complete run,
/// but writes only timeline nodes it creates and Pokémon belonging to the local
/// player. Partner player nodes are never written by this adapter.
/// </summary>
public sealed class VercelSoullockeClient : ITrackerClient
{
    private static readonly TimeSpan RequestTimeout = TimeSpan.FromSeconds(15);
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    private readonly HttpClient _httpClient;
    private readonly AppConfig _config;
    private readonly PokemonSpeciesCatalog _speciesCatalog = new();
    private readonly SemaphoreSlim _initializationLock = new(1, 1);

    private string? _databaseBaseUrl;
    private string? _localPlayerId;
    private string? _partnerPlayerId;
    private string? _partnerPlayerName;
    private string _sessionGameName = string.Empty;
    private VercelRunDocument? _document;
    private bool _initialized;

    public VercelSoullockeClient(HttpClient httpClient, AppConfig config)
    {
        _httpClient = httpClient;
        _config = config;
    }

    public string? PartnerPlayerName => _partnerPlayerName;
    public string SessionGameName => _sessionGameName;
    public bool IsSynchronizationHealthy { get; private set; }

    public async Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var document = _document
            ?? throw new InvalidOperationException("Der soullocke.vercel.app-Run wurde nicht geladen.");
        var player = GetPlayer(document, _localPlayerId!);
        return BuildRun(document, player);
    }

    public async Task<SoullockeRun?> LoadPartnerRunAsync(CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        if (string.IsNullOrWhiteSpace(_partnerPlayerId))
            return null;

        // Partner data is the only remote state that is refreshed continuously.
        var document = await LoadDocumentAsync(cancellationToken);
        var player = GetPlayer(document, _partnerPlayerId);
        var run = BuildRun(document, player);

        SoullockePartnerCatchObserver.ObserveLoadedRuns(
            new Dictionary<string, SoullockeRun>(StringComparer.OrdinalIgnoreCase)
            {
                [_partnerPlayerId] = run
            });

        return run;
    }

    public async Task SaveRunAsync(
        Dictionary<string, SoullockeEncounter> encounters,
        CancellationToken cancellationToken)
    {
        await EnsureInitializedAsync(cancellationToken);
        var document = _document
            ?? throw new InvalidOperationException("Der soullocke.vercel.app-Run wurde nicht geladen.");
        var localPlayer = GetPlayer(document, _localPlayerId!);
        localPlayer.Pokemon ??= new Dictionary<string, VercelPokemon>(StringComparer.OrdinalIgnoreCase);
        document.Timeline ??= new Dictionary<string, VercelTimeline>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in encounters)
        {
            if (pair.Value.Pokemon <= 0)
                continue;

            var origin = FindTimelineOrigin(document, pair.Key);
            if (origin is null)
            {
                origin = Guid.NewGuid().ToString();
                var timeline = new VercelTimeline
                {
                    Key = origin,
                    Index = document.Timeline.Count == 0
                        ? 0
                        : document.Timeline.Values.Max(item => item.Index) + 1,
                    Name = ToTrackerLocationName(pair.Key)
                };

                await PutAsync(
                    $"{RunPath}/timeline/{Escape(origin)}.json",
                    timeline,
                    "neuen Encounter-Ort speichern",
                    cancellationToken);
                document.Timeline[origin] = timeline;
            }

            localPlayer.Pokemon.TryGetValue(origin, out var existing);
            var updated = BuildPokemon(origin, pair.Value, existing);

            await PutAsync(
                $"{RunPath}/players/{Escape(_localPlayerId!)}/pokemon/{Escape(origin)}.json",
                updated,
                "eigenes Pokémon speichern",
                cancellationToken);
            localPlayer.Pokemon[origin] = updated;
        }
    }

    public Task<bool> MarkLinkedPartnerBroFailedAsync(
        string location,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        // Keep the same invariant as the existing provider: SoulBuddy never writes
        // the partner's run. Partner loss states are observed and applied locally.
        return Task.FromResult(false);
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
                throw new InvalidOperationException("Der Tracker-Link enthält keine gültige Run-ID.");

            var document = await LoadDocumentAsync(cancellationToken);
            ResolvePlayers(document);
            _sessionGameName = NormalizeGameName(document.Game);
            _document = document;
            _initialized = true;
        }
        finally
        {
            _initializationLock.Release();
        }
    }

    private async Task<VercelRunDocument> LoadDocumentAsync(CancellationToken cancellationToken)
    {
        if (_databaseBaseUrl is not null)
            return await GetDocumentFromAsync(_databaseBaseUrl, cancellationToken);

        Exception? lastError = null;
        foreach (var candidate in DatabaseCandidates())
        {
            try
            {
                var document = await GetDocumentFromAsync(candidate, cancellationToken);
                _databaseBaseUrl = candidate.TrimEnd('/');
                return document;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                lastError = ex;
            }
        }

        IsSynchronizationHealthy = false;
        throw new InvalidOperationException(
            "Die Firebase-Datenbank von soullocke.vercel.app konnte nicht erreicht werden. " +
            "Falls der Tracker seine Datenbank-URL geändert hat, kann sie über die " +
            "Umgebungsvariable SOULBUDDY_VERCEL_SOULLOCKE_DATABASE_URL gesetzt werden.",
            lastError);
    }

    private async Task<VercelRunDocument> GetDocumentFromAsync(
        string baseUrl,
        CancellationToken cancellationToken)
    {
        using var response = await SendWithTimeoutAsync(
            token => _httpClient.GetAsync(
                $"{baseUrl.TrimEnd('/')}/{Escape(_config.SessionId)}.json",
                token),
            "soullocke.vercel.app-Run laden",
            cancellationToken);

        var body = await response.Content.ReadAsStringAsync(cancellationToken);
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException(
                $"soullocke.vercel.app konnte nicht geladen werden: " +
                $"{(int)response.StatusCode} {body}");
        }

        if (string.Equals(body.Trim(), "null", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException(
                $"Der soullocke.vercel.app-Run '{_config.SessionId}' wurde nicht gefunden.");

        var document = JsonSerializer.Deserialize<VercelRunDocument>(body, JsonOptions)
            ?? throw new InvalidOperationException(
                "soullocke.vercel.app hat ungültige Run-Daten zurückgegeben.");

        document.Players ??= new Dictionary<string, VercelPlayer>(StringComparer.OrdinalIgnoreCase);
        document.Timeline ??= new Dictionary<string, VercelTimeline>(StringComparer.OrdinalIgnoreCase);
        return document;
    }

    private void ResolvePlayers(VercelRunDocument document)
    {
        var matches = document.Players
            .Where(pair => string.Equals(
                pair.Value.Name?.Trim(),
                _config.PlayerName.Trim(),
                StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (matches.Length == 0)
        {
            throw new InvalidOperationException(
                $"Der Spielername '{_config.PlayerName}' wurde in soullocke.vercel.app nicht gefunden. " +
                $"Verfügbare Namen: {string.Join(", ", document.Players.Values.Select(player => player.Name))}");
        }

        if (matches.Length > 1)
        {
            throw new InvalidOperationException(
                $"Der Spielername '{_config.PlayerName}' kommt im Tracker mehrfach vor. " +
                "Eine sichere Zuordnung ist nicht möglich.");
        }

        var local = matches[0];
        _localPlayerId = local.Key;
        _config.PlayerId = local.Key;
        _config.PlayerName = local.Value.Name ?? _config.PlayerName;
        _config.TeamName = _config.PlayerName;

        var partners = document.Players
            .Where(pair => !string.Equals(pair.Key, _localPlayerId, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        if (partners.Length == 1)
        {
            _partnerPlayerId = partners[0].Key;
            _partnerPlayerName = partners[0].Value.Name;
        }
        else
        {
            // SoulBuddy's current UI models one linked partner. Runs with more than
            // two players still load the local player, but no single partner is guessed.
            _partnerPlayerId = null;
            _partnerPlayerName = null;
        }
    }

    private SoullockeRun BuildRun(VercelRunDocument document, VercelPlayer player)
    {
        var encounters = new Dictionary<string, SoullockeEncounter>(StringComparer.OrdinalIgnoreCase);

        foreach (var pair in player.Pokemon ?? [])
        {
            var pokemon = pair.Value;
            var speciesId = _speciesCatalog.ResolveId(pokemon.Name ?? string.Empty);
            if (speciesId <= 0)
            {
                Console.Error.WriteLine(
                    $"soullocke.vercel.app: Pokémon '{pokemon.Name}' konnte keiner National-Dex-ID zugeordnet werden.");
                continue;
            }

            var origin = string.IsNullOrWhiteSpace(pokemon.Origin) ? pair.Key : pokemon.Origin;
            var remoteLocation = document.Timeline.TryGetValue(origin, out var timeline)
                ? timeline.Name
                : origin;
            var displayLocation = FromTrackerLocationName(remoteLocation);

            encounters[displayLocation] = new SoullockeEncounter
            {
                Pokemon = speciesId,
                Nickname = string.IsNullOrWhiteSpace(pokemon.Nickname) ? null : pokemon.Nickname,
                Status = FromTrackerStatus(pokemon)
            };
        }

        return new SoullockeRun
        {
            PlayerId = player.Id ?? string.Empty,
            RunNumber = 1,
            GameName = _sessionGameName,
            Status = "open",
            Encounters = encounters
        };
    }

    private VercelPokemon BuildPokemon(
        string origin,
        SoullockeEncounter encounter,
        VercelPokemon? existing)
    {
        var status = NormalizeStatus(encounter.Status);
        var targetLocation = ToTrackerPokemonLocation(status);
        var speciesSlug = _speciesCatalog.ResolveSlug(encounter.Pokemon);
        var events = existing?.Events is null
            ? new Dictionary<string, VercelPokemonEvent>(StringComparer.OrdinalIgnoreCase)
            : existing.Events.ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.OrdinalIgnoreCase);

        if (existing is null)
        {
            var initialType = status == "notcaught" ? 1 : status == "brofailed" ? 6 : 0;
            events["0"] = new VercelPokemonEvent
            {
                Index = "0",
                Type = initialType,
                Location = origin,
                Details = targetLocation == "grave"
                    ? new VercelEventDetails { Location = "grave" }
                    : null
            };
        }
        else
        {
            var previousStatus = FromTrackerStatus(existing);
            var speciesChanged = !string.Equals(existing.Name, speciesSlug, StringComparison.OrdinalIgnoreCase);
            if (speciesChanged)
            {
                AddEvent(events, 5, origin, new VercelEventDetails { Evolution = speciesSlug });
            }

            if (!string.Equals(previousStatus, status, StringComparison.OrdinalIgnoreCase))
            {
                if (targetLocation == "grave")
                {
                    var type = status switch
                    {
                        "notcaught" => 1,
                        "brofailed" => 6,
                        _ => 3
                    };
                    AddEvent(events, type, origin, new VercelEventDetails { Location = "grave" });
                }
                else
                {
                    AddEvent(events, 2, origin, new VercelEventDetails { Location = targetLocation });
                }
            }
        }

        return new VercelPokemon
        {
            PlayerId = _localPlayerId!,
            Origin = origin,
            Name = speciesSlug,
            Nickname = string.IsNullOrWhiteSpace(encounter.Nickname)
                ? speciesSlug
                : encounter.Nickname!,
            Events = events,
            Location = targetLocation,
            Shiny = existing?.Shiny
        };
    }

    private static void AddEvent(
        Dictionary<string, VercelPokemonEvent> events,
        int type,
        string origin,
        VercelEventDetails? details)
    {
        var key = $"sb-{DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()}-{Guid.NewGuid():N}";
        events[key] = new VercelPokemonEvent
        {
            Index = key,
            Type = type,
            Location = origin,
            Details = details
        };
    }

    private string? FindTimelineOrigin(VercelRunDocument document, string location)
    {
        var wanted = NormalizeLocation(location);
        foreach (var pair in document.Timeline)
        {
            if (NormalizeLocation(pair.Value.Name) == wanted)
                return pair.Key;
        }
        return null;
    }

    private static string NormalizeLocation(string? value)
    {
        var source = (value ?? string.Empty).Trim().ToLowerInvariant();

        var routeMatch = System.Text.RegularExpressions.Regex.Match(
            source,
            @"^(?:(?:johto|kanto)-(?:sea-)?route-|route\s*)(\d+)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (routeMatch.Success)
            return "route" + routeMatch.Groups[1].Value;

        var normalized = new string(source.Where(char.IsLetterOrDigit).ToArray());
        return normalized switch
        {
            "finsterhöhle" or "dunkelhöhle" or "darkcave" => "darkcave",
            "knofensaturm" or "sprouttower" => "sprouttower",
            "newborkia" or "newbarktown" or "starter" => "starter",
            _ => normalized
        };
    }

    private static string FromTrackerLocationName(string? value)
    {
        var source = (value ?? string.Empty).Trim();
        if (string.IsNullOrWhiteSpace(source))
            return "Unbekannter Ort";

        var lower = source.ToLowerInvariant();
        var routeMatch = System.Text.RegularExpressions.Regex.Match(
            lower,
            @"^(?:johto|kanto)-(?:sea-)?route-(\d+)$",
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (routeMatch.Success)
            return $"Route {routeMatch.Groups[1].Value}";

        return lower switch
        {
            "dark-cave" => "Dark Cave",
            "sprout-tower" => "Sprout Tower",
            "new-bark-town" => "Starter",
            _ => string.Join(' ', source.Split('-', StringSplitOptions.RemoveEmptyEntries)
                .Select(part => char.ToUpperInvariant(part[0]) + part[1..]))
        };
    }

    private static string ToTrackerLocationName(string value)
    {
        var source = value.Trim();
        var routeMatch = System.Text.RegularExpressions.Regex.Match(
            source,
            @"^Route\s+(\d+)$",
            System.Text.RegularExpressions.RegexOptions.IgnoreCase |
            System.Text.RegularExpressions.RegexOptions.CultureInvariant);
        if (routeMatch.Success)
        {
            var number = int.Parse(routeMatch.Groups[1].Value);
            if (number is >= 29 and <= 46)
                return $"johto-route-{number}";
            if (number is >= 1 and <= 28)
                return $"kanto-route-{number}";
        }

        return NormalizeLocation(source) switch
        {
            "darkcave" => "dark-cave",
            "sprouttower" => "sprout-tower",
            "starter" => "new-bark-town",
            _ => source
        };
    }

    private static string FromTrackerStatus(VercelPokemon pokemon)
    {
        var location = (pokemon.Location ?? "box").Trim().ToLowerInvariant();
        if (location is "team")
            return "alive";
        if (location is "box" or "daycare")
            return "boxed";
        if (location != "grave")
            return "alive";

        var eventTypes = (pokemon.Events ?? [])
            .Values
            .Select(item => item.Type)
            .ToHashSet();
        if (eventTypes.Contains(1))
            return "notcaught";
        if (eventTypes.Contains(6))
            return "brofailed";
        return "fainted";
    }

    private static string ToTrackerPokemonLocation(string status) => status switch
    {
        "boxed" => "box",
        "fainted" or "notcaught" or "brofailed" => "grave",
        _ => "team"
    };

    private static string NormalizeStatus(string? status) =>
        (status ?? "alive").Trim().ToLowerInvariant() switch
        {
            "fainted" => "fainted",
            "notcaught" or "not-caught" or "not-catched" => "notcaught",
            "brofailed" or "bro-failed" => "brofailed",
            "boxed" or "box" => "boxed",
            _ => "alive"
        };

    private static string NormalizeGameName(string? gameName)
    {
        var normalized = (gameName ?? string.Empty).Trim().ToLowerInvariant()
            .Replace(" ", string.Empty, StringComparison.Ordinal)
            .Replace("-", string.Empty, StringComparison.Ordinal);
        if (string.IsNullOrWhiteSpace(normalized))
            throw new InvalidOperationException("Der Tracker enthält kein gültiges Spiel.");

        return normalized switch
        {
            "hg" or "heartgold" => "heartgold",
            "ss" or "soulsilver" => "soulsilver",
            _ => normalized
        };
    }

    private async Task PutAsync<T>(
        string relativePath,
        T value,
        string operation,
        CancellationToken cancellationToken)
    {
        var baseUrl = _databaseBaseUrl
            ?? throw new InvalidOperationException("Die Firebase-Datenbank wurde noch nicht aufgelöst.");
        using var response = await SendWithTimeoutAsync(
            token => _httpClient.PutAsJsonAsync(
                $"{baseUrl.TrimEnd('/')}/{relativePath}",
                value,
                JsonOptions,
                token),
            operation,
            cancellationToken);

        if (!response.IsSuccessStatusCode)
        {
            var body = await response.Content.ReadAsStringAsync(cancellationToken);
            throw new HttpRequestException(
                $"soullocke.vercel.app konnte nicht aktualisiert werden: " +
                $"{(int)response.StatusCode} {body}");
        }
    }

    private async Task<HttpResponseMessage> SendWithTimeoutAsync(
        Func<CancellationToken, Task<HttpResponseMessage>> send,
        string operation,
        CancellationToken cancellationToken)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(RequestTimeout);
        try
        {
            var response = await send(timeout.Token);
            IsSynchronizationHealthy = response.IsSuccessStatusCode;
            return response;
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            IsSynchronizationHealthy = false;
            throw new TimeoutException(
                $"Zeitüberschreitung beim Vorgang '{operation}' nach " +
                $"{RequestTimeout.TotalSeconds:0} Sekunden.");
        }
        catch
        {
            IsSynchronizationHealthy = false;
            throw;
        }
    }

    private string RunPath => Escape(_config.SessionId);

    private static VercelPlayer GetPlayer(VercelRunDocument document, string playerId) =>
        document.Players.TryGetValue(playerId, out var player)
            ? player
            : throw new InvalidOperationException($"Tracker-Spieler '{playerId}' wurde nicht gefunden.");

    private static IEnumerable<string> DatabaseCandidates()
    {
        var configured = Environment.GetEnvironmentVariable("SOULBUDDY_VERCEL_SOULLOCKE_DATABASE_URL");
        if (!string.IsNullOrWhiteSpace(configured))
            yield return configured.TrimEnd('/');

        // The upstream project id is soullocke-f7500 and its exported default RTDB
        // is named soullocke-f7500-default-rtdb. Keep regional URLs as fallbacks so
        // the adapter remains usable if Firebase changes the public endpoint shape.
        yield return "https://soullocke-f7500-default-rtdb.firebaseio.com";
        yield return "https://soullocke-f7500-default-rtdb.europe-west1.firebasedatabase.app";
        yield return "https://soullocke-f7500-default-rtdb.asia-southeast1.firebasedatabase.app";
        yield return "https://soullocke-f7500-default-rtdb.firebasedatabase.app";
    }

    private static string Escape(string value) => Uri.EscapeDataString(value);

    private sealed class VercelRunDocument
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("password")]
        public string? Password { get; set; }

        [JsonPropertyName("game")]
        public string? Game { get; set; }

        [JsonPropertyName("region")]
        public string? Region { get; set; }

        [JsonPropertyName("timeline")]
        public Dictionary<string, VercelTimeline> Timeline { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("players")]
        public Dictionary<string, VercelPlayer> Players { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class VercelTimeline
    {
        [JsonPropertyName("key")]
        public string Key { get; set; } = string.Empty;

        [JsonPropertyName("index")]
        public int Index { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; } = string.Empty;

        [JsonPropertyName("notes")]
        public string? Notes { get; set; }
    }

    private sealed class VercelPlayer
    {
        [JsonPropertyName("id")]
        public string? Id { get; set; }

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("pokemon")]
        public Dictionary<string, VercelPokemon>? Pokemon { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class VercelPokemon
    {
        [JsonPropertyName("playerId")]
        public string PlayerId { get; set; } = string.Empty;

        [JsonPropertyName("origin")]
        public string Origin { get; set; } = string.Empty;

        [JsonPropertyName("name")]
        public string? Name { get; set; }

        [JsonPropertyName("nickname")]
        public string? Nickname { get; set; }

        [JsonPropertyName("events")]
        public Dictionary<string, VercelPokemonEvent>? Events { get; set; } =
            new(StringComparer.OrdinalIgnoreCase);

        [JsonPropertyName("location")]
        public string? Location { get; set; }

        [JsonPropertyName("shiny")]
        public bool? Shiny { get; set; }
    }

    private sealed class VercelPokemonEvent
    {
        [JsonPropertyName("index")]
        public string Index { get; set; } = string.Empty;

        [JsonPropertyName("type")]
        public int Type { get; set; }

        [JsonPropertyName("location")]
        public string Location { get; set; } = string.Empty;

        [JsonPropertyName("details")]
        public VercelEventDetails? Details { get; set; }
    }

    private sealed class VercelEventDetails
    {
        [JsonPropertyName("location")]
        public string? Location { get; set; }

        [JsonPropertyName("evolution")]
        public string? Evolution { get; set; }
    }
}
