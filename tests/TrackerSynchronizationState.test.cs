using Microsoft.Data.Sqlite;
using System.Net;
using System.Text;
using SoulBuddy.Data;
using SoulBuddy.Models;
using SoulBuddy.Services;
using SoulBuddy.Sources;
using Xunit;

namespace SoulBuddy.Tests;

public sealed class TrackerSynchronizationStateTests
{
    [Fact]
    public async Task InitializeAsync_ReportsInitializingUntilInitialReadsComplete()
    {
        var tracker = new ControlledTrackerClient();
        var (service, databasePath) = CreateService(tracker);

        try
        {
            var initialization = service.InitializeAsync(CancellationToken.None);
            await WaitForStateAsync(service, TrackerSynchronizationState.Initializing);

            Assert.False(initialization.IsCompleted);
            Assert.Equal(TrackerSynchronizationState.Initializing, service.SynchronizationState);

            tracker.CompleteLocalRun();
            await initialization;

            Assert.Equal(TrackerSynchronizationState.Healthy, service.SynchronizationState);
            Assert.True(service.IsServerSynchronizationHealthy);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task InitializeAsync_ReportsFailedAfterActualInitializationError()
    {
        var tracker = new ControlledTrackerClient(
            new InvalidOperationException("invalid tracker document"));
        var (service, databasePath) = CreateService(tracker);

        try
        {
            await Assert.ThrowsAsync<InvalidOperationException>(
                () => service.InitializeAsync(CancellationToken.None));

            Assert.Equal(TrackerSynchronizationState.Failed, service.SynchronizationState);
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Fact]
    public async Task RunAsync_RetriesTransientInitializationAndRecoversToHealthy()
    {
        var tracker = new FlakyTrackerClient();
        var (service, databasePath) = CreateService(tracker, pollIntervalMilliseconds: 10);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        try
        {
            var run = service.RunAsync(cancellation.Token);
            await WaitForStateAsync(service, TrackerSynchronizationState.Healthy);

            Assert.Equal(2, tracker.LocalLoadAttempts);
            cancellation.Cancel();
            try
            {
                await run;
            }
            catch (OperationCanceledException)
            {
            }
        }
        finally
        {
            DeleteDatabase(databasePath);
        }
    }

    [Theory]
    [InlineData("https://soullocke.vercel.app/run/example-run", TrackerProvider.SoullockeVercel)]
    [InlineData("https://soullocke.com/session/example-session", TrackerProvider.SoullockeDotCom)]
    public void Parse_SelectsExpectedProvider(string link, TrackerProvider expectedProvider)
    {
        var result = TrackerLinkParser.Parse(link);

        Assert.Equal(expectedProvider, result.Provider);
    }

    [Fact]
    public async Task VercelClient_CompletesInitialReadPartnerRefreshAndLocalWrite()
    {
        const string overrideName = "SOULBUDDY_VERCEL_SOULLOCKE_DATABASE_URL";
        var previousOverride = Environment.GetEnvironmentVariable(overrideName);
        var handler = new RecordingVercelHandler();
        Environment.SetEnvironmentVariable(overrideName, "https://firebase.test");

        try
        {
            using var httpClient = new HttpClient(handler);
            var config = new AppConfig
            {
                PartyJsonPath = "unused.json",
                SessionId = "test-run",
                PlayerName = "Alice",
                TrackerProvider = TrackerProvider.SoullockeVercel,
                SoullockeEnabled = true,
                DryRun = false
            };
            var client = new VercelTrackerClient(httpClient, config);

            var local = await client.LoadRunAsync(CancellationToken.None);
            var partner = await client.LoadPartnerRunAsync(CancellationToken.None);
            await client.SaveRunAsync(local.Encounters, CancellationToken.None);

            Assert.True(client.IsSynchronizationHealthy);
            Assert.Equal("Bob", client.PartnerPlayerName);
            Assert.Single(local.Encounters);
            Assert.NotNull(partner);
            Assert.Single(partner!.Encounters);
            Assert.Equal(2, handler.GetCount);
            Assert.Equal(1, handler.PutCount);
            Assert.Contains("/test-run/players/alice/pokemon/origin.json", handler.LastPutPath);
            Assert.DoesNotContain("players/bob", handler.LastPutPath, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Environment.SetEnvironmentVariable(overrideName, previousOverride);
        }
    }

    private static (SyncService Service, string DatabasePath) CreateService(
        ITrackerClient tracker,
        int pollIntervalMilliseconds = 1000)
    {
        var databasePath = Path.Combine(
            Path.GetTempPath(),
            $"soulbuddy-sync-state-{Guid.NewGuid():N}.db");
        var config = new AppConfig
        {
            PartyJsonPath = "unused.json",
            SessionId = "secret-run-id",
            PlayerName = "Player",
            TrackerProvider = TrackerProvider.SoullockeVercel,
            SoullockeEnabled = true,
            DryRun = false,
            PollIntervalMilliseconds = pollIntervalMilliseconds
        };
        var mapper = new LocationMapper();
        var service = new SyncService(
            new EmptyPartySource(),
            new KnownPokemonStore(databasePath),
            tracker,
            mapper,
            new NuzlockeRuleEventSource(mapper),
            config);
        return (service, databasePath);
    }

    private static async Task WaitForStateAsync(
        SyncService service,
        TrackerSynchronizationState state)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (service.SynchronizationState != state)
            await Task.Delay(10, timeout.Token);
    }

    private static void DeleteDatabase(string databasePath)
    {
        SqliteConnection.ClearAllPools();
        foreach (var path in new[] { databasePath, databasePath + "-wal", databasePath + "-shm" })
            File.Delete(path);
    }

    private sealed class EmptyPartySource : IPartySource
    {
        public Task<IReadOnlyList<PartySlot>> ReadPartyAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PartySlot>>([]);

        public Task<IReadOnlyList<PartySlot>> ReadAllPokemonAsync(CancellationToken cancellationToken) =>
            Task.FromResult<IReadOnlyList<PartySlot>>([]);
    }

    private sealed class ControlledTrackerClient : ITrackerClient
    {
        private readonly TaskCompletionSource<SoullockeRun> _localRun =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public ControlledTrackerClient(Exception? failure = null)
        {
            if (failure is not null)
                _localRun.SetException(failure);
        }

        public string? PartnerPlayerName => "Partner";
        public string SessionGameName => "heartgold";
        public bool IsSynchronizationHealthy { get; private set; }

        public void CompleteLocalRun()
        {
            IsSynchronizationHealthy = true;
            _localRun.TrySetResult(new SoullockeRun
            {
                PlayerId = "local",
                RunNumber = 1,
                GameName = "heartgold",
                Status = "open"
            });
        }

        public Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken) =>
            _localRun.Task.WaitAsync(cancellationToken);

        public Task<SoullockeRun?> LoadPartnerRunAsync(CancellationToken cancellationToken) =>
            Task.FromResult<SoullockeRun?>(null);

        public Task SaveRunAsync(
            Dictionary<string, SoullockeEncounter> encounters,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> MarkLinkedPartnerBroFailedAsync(
            string location,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }

    private sealed class RecordingVercelHandler : HttpMessageHandler
    {
        private const string RunJson = """
            {
              "game": "HeartGold",
              "timeline": {
                "origin": { "key": "origin", "index": 0, "name": "johto-route-29" }
              },
              "players": {
                "alice": {
                  "id": "alice",
                  "name": "Alice",
                  "pokemon": {
                    "origin": {
                      "playerId": "alice",
                      "origin": "origin",
                      "name": "chikorita",
                      "nickname": "Leaf",
                      "location": "team",
                      "events": [
                        { "index": 0, "type": 0, "location": "origin" }
                      ]
                    }
                  }
                },
                "bob": {
                  "id": "bob",
                  "name": "Bob",
                  "pokemon": {
                    "origin": {
                      "playerId": "bob",
                      "origin": "origin",
                      "name": "cyndaquil",
                      "nickname": "Flame",
                      "location": "team",
                      "events": [
                        { "index": "0", "type": 0, "location": "origin" }
                      ]
                    }
                  }
                }
              }
            }
            """;

        public int GetCount { get; private set; }
        public int PutCount { get; private set; }
        public string LastPutPath { get; private set; } = string.Empty;

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            if (request.Method == HttpMethod.Get)
            {
                GetCount++;
                return JsonResponse(RunJson);
            }

            if (request.Method == HttpMethod.Put)
            {
                PutCount++;
                LastPutPath = request.RequestUri?.AbsolutePath ?? string.Empty;
                _ = request.Content is null
                    ? string.Empty
                    : await request.Content.ReadAsStringAsync(cancellationToken);
                return JsonResponse("{}");
            }

            return new HttpResponseMessage(HttpStatusCode.MethodNotAllowed);
        }

        private static HttpResponseMessage JsonResponse(string json) => new(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
    }

    private sealed class FlakyTrackerClient : ITrackerClient
    {
        public int LocalLoadAttempts { get; private set; }
        public string? PartnerPlayerName => "Partner";
        public string SessionGameName => "heartgold";
        public bool IsSynchronizationHealthy { get; private set; }

        public Task<SoullockeRun> LoadRunAsync(CancellationToken cancellationToken)
        {
            LocalLoadAttempts++;
            if (LocalLoadAttempts == 1)
                throw new TimeoutException("temporary timeout");

            IsSynchronizationHealthy = true;
            return Task.FromResult(new SoullockeRun
            {
                PlayerId = "local",
                RunNumber = 1,
                GameName = "heartgold",
                Status = "open"
            });
        }

        public Task<SoullockeRun?> LoadPartnerRunAsync(CancellationToken cancellationToken) =>
            Task.FromResult<SoullockeRun?>(null);

        public Task SaveRunAsync(
            Dictionary<string, SoullockeEncounter> encounters,
            CancellationToken cancellationToken) => Task.CompletedTask;

        public Task<bool> MarkLinkedPartnerBroFailedAsync(
            string location,
            CancellationToken cancellationToken) => Task.FromResult(false);
    }
}
