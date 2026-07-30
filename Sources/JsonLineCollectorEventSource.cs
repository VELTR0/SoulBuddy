using System.Text.Json;
using SoulBuddy.Models;

namespace SoulBuddy.Sources;

public sealed class JsonLineCollectorEventSource
{
    private readonly string _eventFilePath;

    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    public JsonLineCollectorEventSource(string eventFilePath)
    {
        _eventFilePath = eventFilePath;
    }

    public async Task RunAsync(
        CancellationToken cancellationToken)
    {
        Console.WriteLine(
            $"Collector-Ereignisse: {_eventFilePath}");

        Console.WriteLine(
            "Warte auf Nachrichten vom Emulator.");

        while (!cancellationToken.IsCancellationRequested)
        {
            if (!File.Exists(_eventFilePath))
            {
                await Task.Delay(
                    500,
                    cancellationToken);

                continue;
            }

            try
            {
                await ReadEventsAsync(cancellationToken);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch (IOException)
            {
                await Task.Delay(
                    500,
                    cancellationToken);
            }
            catch (UnauthorizedAccessException ex)
            {
                Console.WriteLine(
                    $"Zugriff auf die Event-Datei nicht möglich: " +
                    $"{ex.Message}");

                await Task.Delay(
                    1000,
                    cancellationToken);
            }
        }
    }

    private async Task ReadEventsAsync(
        CancellationToken cancellationToken)
    {
        await using var stream = new FileStream(
            _eventFilePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);

        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(
                cancellationToken);

            if (line is null)
            {
                await Task.Delay(
                    250,
                    cancellationToken);

                continue;
            }

            ProcessLine(line);
        }
    }

    private void ProcessLine(string line)
    {
        if (string.IsNullOrWhiteSpace(line))
        {
            return;
        }

        CollectorEvent? collectorEvent;

        try
        {
            collectorEvent =
                JsonSerializer.Deserialize<CollectorEvent>(
                    line,
                    _jsonOptions);
        }
        catch (JsonException ex)
        {
            Console.WriteLine(
                $"Ungültige Collector-Nachricht: {ex.Message}");

            Console.WriteLine(
                $"  Inhalt: {line}");

            return;
        }

        if (collectorEvent is null)
        {
            Console.WriteLine(
                "Leere Collector-Nachricht empfangen.");

            return;
        }

        HandleEvent(collectorEvent);
    }

    private static void HandleEvent(
        CollectorEvent collectorEvent)
    {
        switch (collectorEvent.Type)
        {
            case "collector-started":
                HandleCollectorStarted(collectorEvent);
                break;

            case "party-update":
                HandlePartyUpdate(collectorEvent);
                break;

            default:
                Console.WriteLine(
                    $"[{DateTime.Now:HH:mm:ss}] " +
                    $"Collector-Ereignis empfangen: " +
                    $"{collectorEvent.Type}");

                break;
        }
    }

    private static void HandleCollectorStarted(
        CollectorEvent collectorEvent)
    {
        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] " +
            $"Collector erkannt. " +
            $"Spiel: {collectorEvent.Game ?? "unbekannt"}, " +
            $"Protokoll: {collectorEvent.ProtocolVersion}");
    }

    private static void HandlePartyUpdate(
        CollectorEvent collectorEvent)
    {
        var generationText =
            collectorEvent.Generation?.ToString()
            ?? "unbekannt";

        Console.WriteLine(
            $"[{DateTime.Now:HH:mm:ss}] " +
            $"Party-Update empfangen. " +
            $"Spiel: {collectorEvent.Game ?? "unbekannt"}, " +
            $"Generation: {generationText}, " +
            $"Slots: {collectorEvent.Slots.Count}");

        foreach (var slot in collectorEvent.Slots)
        {
            PrintSlot(slot);
        }
    }

    private static void PrintSlot(PartySlot slot)
    {
        if (slot.Pokemon is null)
        {
            Console.WriteLine(
                $"  Slot {slot.SlotId}: leer " +
                $"(Change-ID: {slot.ChangeId})");

            return;
        }

        var pokemon = slot.Pokemon;

        var displayName =
            string.IsNullOrWhiteSpace(pokemon.Nickname)
                ? pokemon.SpeciesName
                : $"{pokemon.Nickname} ({pokemon.SpeciesName})";

        Console.WriteLine(
            $"  Slot {slot.SlotId}: " +
            $"{displayName}, " +
            $"Level {pokemon.Level}, " +
            $"KP {pokemon.Hp.Current}/{pokemon.Hp.Max}, " +
            $"PID {pokemon.Pid}, " +
            $"Change-ID {slot.ChangeId}");
    }
}