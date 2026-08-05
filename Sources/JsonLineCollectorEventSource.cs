using System.Text.Json;
using SoulBuddy.Models;

namespace SoulBuddy.Sources;

public sealed class JsonLineCollectorEventSource
{
    private readonly string _eventFilePath;
    private readonly LivePartySource _partySource;
    private readonly PlayerLiveStateSource _liveStateSource;
    private readonly NuzlockeRuleEventSource _ruleEventSource;
    private long _readPosition;
    private bool _readPositionInitialized;

    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public JsonLineCollectorEventSource(
        string eventFilePath,
        LivePartySource partySource,
        PlayerLiveStateSource liveStateSource,
        NuzlockeRuleEventSource ruleEventSource)
    {
        _eventFilePath = eventFilePath;
        _partySource = partySource;
        _liveStateSource = liveStateSource;
        _ruleEventSource = ruleEventSource;
    }

    public async Task RunAsync(CancellationToken cancellationToken)
    {
        while (!cancellationToken.IsCancellationRequested)
        {
            try
            {
                if (!File.Exists(_eventFilePath))
                {
                    await Task.Delay(500, cancellationToken);
                    continue;
                }

                InitializeReadPosition();
                await ReadAvailableEventsAsync(cancellationToken);
                await Task.Delay(250, cancellationToken);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                await Task.Delay(500, cancellationToken);
            }
            catch (UnauthorizedAccessException)
            {
                await Task.Delay(1000, cancellationToken);
            }
        }
    }

    private void InitializeReadPosition()
    {
        if (_readPositionInitialized)
            return;

        _readPosition = new FileInfo(_eventFilePath).Length;
        _readPositionInitialized = true;
    }

    private async Task ReadAvailableEventsAsync(CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            _eventFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        if (_readPosition > stream.Length)
            _readPosition = 0;

        stream.Seek(_readPosition, SeekOrigin.Begin);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var lineStartPosition = stream.Position;
            var line = await reader.ReadLineAsync(cancellationToken);

            if (line is null)
            {
                _readPosition = stream.Position;
                break;
            }

            _readPosition = stream.Position;

            try
            {
                await ProcessLineAsync(line, cancellationToken);
            }
            catch
            {
                _readPosition = lineStartPosition;
                throw;
            }
        }
    }

    private async Task ProcessLineAsync(string line, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(line))
            return;

        CollectorEvent? collectorEvent;
        try
        {
            collectorEvent = JsonSerializer.Deserialize<CollectorEvent>(line, _jsonOptions);
        }
        catch (JsonException)
        {
            return;
        }

        if (collectorEvent is not null)
            await HandleEventAsync(collectorEvent, cancellationToken);
    }

    private async Task HandleEventAsync(
        CollectorEvent collectorEvent,
        CancellationToken cancellationToken)
    {
        switch (collectorEvent.Type)
        {
            case "collector-started":
                Console.WriteLine(
                    $"Spiel wurde live verbunden: {collectorEvent.Game ?? "unbekannt"}.");
                break;

            case "party-update":
                _ruleEventSource.ObservePokemonUpdate(collectorEvent.Slots);
                await _partySource.ApplyUpdateAsync(collectorEvent.Slots, cancellationToken);
                break;

            case "box-update":
                _ruleEventSource.ObservePokemonUpdate(collectorEvent.Slots);
                await _partySource.ApplyBoxUpdateAsync(collectorEvent.Slots, cancellationToken);
                break;

            case "player-state" when collectorEvent.State is not null:
                _ruleEventSource.ObservePlayerState(collectorEvent.State);
                _liveStateSource.Apply(collectorEvent.State);
                break;
        }
    }
}
