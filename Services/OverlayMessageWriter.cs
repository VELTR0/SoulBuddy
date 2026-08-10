using System.Text.Json;
using System.Text.RegularExpressions;
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
    private readonly IReadOnlyDictionary<int, string> _speciesNames;

    public OverlayMessageWriter(string filePath)
    {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");
        File.WriteAllText(_filePath, string.Empty);
        _speciesNames = LoadSpeciesNames(filePath);

        // This writer is created before the first Soullocke run is loaded. Resetting
        // here means the first response for local player and partner becomes a quiet
        // baseline; only later partner additions produce an overlay notification.
        SoullockePartnerCatchObserver.ResetAndSetHandler(WritePartnerCatch);
    }

    public void Write(NuzlockeRuleEvent ruleEvent) =>
        WriteMessage(FormatMessage(ruleEvent));

    private void WritePartnerCatch(SoullockePartnerCatchDetected partnerCatch)
    {
        var species = _speciesNames.TryGetValue(partnerCatch.Pokemon, out var resolved)
            ? resolved
            : $"Pokémon #{partnerCatch.Pokemon}";
        var name = string.IsNullOrWhiteSpace(partnerCatch.Nickname)
            ? species
            : partnerCatch.Nickname!;

        WriteMessage($"Partner hat {name} gefangen!");
        Console.WriteLine(
            $"SoulLink-Event: Partner hat {name} in {partnerCatch.Location} gefangen.");
    }

    private void WriteMessage(string message)
    {
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

    private static IReadOnlyDictionary<int, string> LoadSpeciesNames(string overlayFilePath)
    {
        try
        {
            var runtimeDirectory = Path.GetDirectoryName(Path.GetFullPath(overlayFilePath));
            var projectDirectory = runtimeDirectory is null
                ? null
                : Directory.GetParent(runtimeDirectory)?.FullName;
            if (projectDirectory is null)
                return new Dictionary<int, string>();

            var mappingPath = Path.Combine(
                projectDirectory,
                "collectors",
                "desmume-gen4",
                "pokemon_name_to_pokedex_id.lua");
            if (!File.Exists(mappingPath))
                return new Dictionary<int, string>();

            var result = new Dictionary<int, string>();
            var pattern = new Regex(
                "^\\s*\\[\\\"(?<name>.+?)\\\"\\]\\s*=\\s*(?<id>\\d+)\\s*,?",
                RegexOptions.Compiled);

            foreach (var line in File.ReadLines(mappingPath))
            {
                var match = pattern.Match(line);
                if (!match.Success ||
                    !int.TryParse(match.Groups["id"].Value, out var id) ||
                    id <= 0)
                {
                    continue;
                }

                result[id] = match.Groups["name"].Value;
            }

            return result;
        }
        catch
        {
            // Missing name data must never prevent overlay delivery. The caller
            // falls back to the Pokédex number when the table cannot be read.
            return new Dictionary<int, string>();
        }
    }

    private sealed class OverlayMessage
    {
        public string Message { get; init; } = string.Empty;
        public int DurationSeconds { get; init; }
    }
}
