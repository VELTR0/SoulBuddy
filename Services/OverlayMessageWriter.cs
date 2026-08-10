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
            DurationSeconds = 7
        }, JsonOptions);

        lock (_sync)
        {
            File.AppendAllText(_filePath, line + Environment.NewLine);
        }
    }

    private static string FormatMessage(NuzlockeRuleEvent ruleEvent)
    {
        var name = ShortPokemonName(ruleEvent.SpeciesName, ruleEvent.Nickname);

        return ruleEvent.Type switch
        {
            NuzlockeRuleEventType.PokemonKnockedOut =>
                $"{name} ist K.O.!",

            NuzlockeRuleEventType.PartnerPokemonKnockedOut =>
                FormatPartnerKnockout(ruleEvent),

            NuzlockeRuleEventType.CatchableEncounter when ruleEvent.IsShiny =>
                $"Shiny {name} fangbar!",

            NuzlockeRuleEventType.CatchableEncounter =>
                $"{name} fangbar!",

            NuzlockeRuleEventType.CatchSucceeded =>
                $"{name} gefangen!",

            NuzlockeRuleEventType.CatchFailed =>
                "Fang fehlgeschlagen!",

            _ => $"{name}: Nuzlocke-Event"
        };
    }

    private static string FormatPartnerKnockout(NuzlockeRuleEvent ruleEvent)
    {
        var linkedName = string.IsNullOrWhiteSpace(ruleEvent.LinkedSpeciesName)
            ? "Pokemon"
            : ShortPokemonName(ruleEvent.LinkedSpeciesName, ruleEvent.LinkedNickname);

        return $"Partner K.O. - {linkedName} raus!";
    }

    private static string ShortPokemonName(string species, string? nickname) =>
        string.IsNullOrWhiteSpace(nickname) ? species : nickname!;

    private sealed class OverlayMessage
    {
        public string Message { get; init; } = string.Empty;
        public int DurationSeconds { get; init; }
    }
}
