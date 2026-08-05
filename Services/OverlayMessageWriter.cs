using System.Text.Json;
using SoulBuddy.Models;

namespace SoulBuddy.Services;

public sealed class OverlayMessageWriter
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase
    };

    private readonly string _filePath;
    private readonly object _sync = new();

    public OverlayMessageWriter(string filePath)
    {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");
        File.WriteAllText(_filePath, string.Empty);
    }

    public void Write(NuzlockeRuleEvent ruleEvent)
    {
        var message = FormatMessage(ruleEvent);
        var line = JsonSerializer.Serialize(new OverlayMessage
        {
            Message = message,
            DurationSeconds = 4
        }, JsonOptions);

        lock (_sync)
        {
            File.AppendAllText(_filePath, line + Environment.NewLine);
        }
    }

    private static string FormatMessage(NuzlockeRuleEvent ruleEvent)
    {
        var name = string.IsNullOrWhiteSpace(ruleEvent.Nickname)
            ? ruleEvent.SpeciesName
            : $"{ruleEvent.Nickname} ({ruleEvent.SpeciesName})";

        return ruleEvent.Type switch
        {
            NuzlockeRuleEventType.PokemonKnockedOut =>
                $"{name} ist K.O. gegangen.",

            NuzlockeRuleEventType.CatchableEncounter when ruleEvent.IsShiny && ruleEvent.IsFirstEncounter =>
                $"{name} ist fangbar: erster Encounter und Shiny.",

            NuzlockeRuleEventType.CatchableEncounter when ruleEvent.IsShiny =>
                $"{name} ist als Shiny fangbar.",

            NuzlockeRuleEventType.CatchableEncounter =>
                $"{name} ist der erste Encounter und fangbar.",

            NuzlockeRuleEventType.CatchSucceeded =>
                $"{name} wurde gefangen.",

            NuzlockeRuleEventType.CatchFailed =>
                $"Der Fang von {name} ist missglückt.",

            _ => $"Nuzlocke-Ereignis: {name}."
        };
    }

    private sealed class OverlayMessage
    {
        public string Message { get; init; } = string.Empty;
        public int DurationSeconds { get; init; }
    }
}
