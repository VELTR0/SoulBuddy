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
    private readonly Dictionary<string, DateTimeOffset> _recentMessages =
        new(StringComparer.Ordinal);

    public OverlayMessageWriter(string filePath)
    {
        _filePath = filePath;
        Directory.CreateDirectory(Path.GetDirectoryName(filePath) ?? ".");
        File.WriteAllText(_filePath, string.Empty);
        _speciesNames = LoadSpeciesNames(filePath);

        // The first loaded response for each player is a quiet baseline. Later
        // partner catches and failed catches are surfaced from SoulLocke snapshots.
        SoullockePartnerCatchObserver.ResetAndSetHandler(WritePartnerCatch);
        SoullockePartnerCatchObserver.SetFailureHandler(WritePartnerCatchFailed);
    }

    public void Write(NuzlockeRuleEvent ruleEvent)
    {
        // Own K.O. and boxing events still exist for state/synchronization purposes,
        // but they are not useful as local overlay notifications. The linked player
        // receives the corresponding partner notification through SoulLocke.
        if (ruleEvent.Type is
            NuzlockeRuleEventType.PokemonBoxed or
            NuzlockeRuleEventType.PokemonKnockedOut)
        {
            return;
        }

        WriteMessage(FormatMessage(ruleEvent));
    }

    private void WritePartnerCatch(SoullockePartnerCatchDetected partnerCatch)
    {
        var species = ResolveSpeciesName(partnerCatch.Pokemon, null);
        var name = string.IsNullOrWhiteSpace(partnerCatch.Nickname)
            ? species
            : partnerCatch.Nickname!;
        var location = NormalizePartnerLocation(partnerCatch.Location);

        WriteMessage($"Partner hat {name} gefangen! ({location})");
        Console.WriteLine(
            $"SoulLink-Event: Partner hat {name} in {location} gefangen.");
    }

    private void WritePartnerCatchFailed(SoullockePartnerCatchFailedDetected partnerCatch)
    {
        var species = ResolveSpeciesName(partnerCatch.Pokemon, null);
        var name = string.IsNullOrWhiteSpace(partnerCatch.Nickname)
            ? species
            : partnerCatch.Nickname!;
        var location = NormalizePartnerLocation(partnerCatch.Location);

        WriteMessage($"Partner konnte {name} nicht fangen! ({location})");
        Console.WriteLine(
            $"SoulLink-Event: Partner konnte {name} in {location} nicht fangen.");
    }

    private void WriteMessage(string message)
    {
        lock (_sync)
        {
            var now = DateTimeOffset.UtcNow;
            foreach (var stale in _recentMessages
                         .Where(pair => now - pair.Value > TimeSpan.FromSeconds(3))
                         .Select(pair => pair.Key)
                         .ToArray())
            {
                _recentMessages.Remove(stale);
            }

            // Suppress an identical notification if the same state is observed more
            // than once within a short polling window.
            if (_recentMessages.TryGetValue(message, out var previous) &&
                now - previous <= TimeSpan.FromSeconds(3))
            {
                return;
            }

            _recentMessages[message] = now;

            var line = JsonSerializer.Serialize(new OverlayMessage
            {
                Message = message,
                DurationSeconds = 7
            }, JsonOptions);

            File.AppendAllText(_filePath, line + Environment.NewLine);
        }
    }

    private string FormatMessage(NuzlockeRuleEvent ruleEvent)
    {
        var name = ShortPokemonName(ruleEvent.SpeciesName, ruleEvent.Nickname);
        var location = string.IsNullOrWhiteSpace(ruleEvent.LocationName)
            ? "Unbekannter Ort"
            : ruleEvent.LocationName.Trim();

        return ruleEvent.Type switch
        {
            NuzlockeRuleEventType.PartnerPokemonKnockedOut =>
                FormatPartnerKnockout(ruleEvent),

            NuzlockeRuleEventType.PartnerPokemonBoxed =>
                FormatPartnerBoxed(ruleEvent),

            NuzlockeRuleEventType.CatchableEncounter when ruleEvent.IsShiny =>
                $"Shiny {name} fangbar! ({location})",

            NuzlockeRuleEventType.CatchableEncounter =>
                $"{name} fangbar! ({location})",

            NuzlockeRuleEventType.CatchSucceeded =>
                $"{name} gefangen! ({location})",

            NuzlockeRuleEventType.CatchFailed =>
                $"Fang fehlgeschlagen! ({location})",

            _ => $"{name}: Nuzlocke-Event"
        };
    }

    private string FormatPartnerKnockout(NuzlockeRuleEvent ruleEvent)
    {
        var partnerSpecies = ResolveSpeciesName(ruleEvent.SpeciesId, ruleEvent.SpeciesName);
        var partnerName = string.IsNullOrWhiteSpace(ruleEvent.Nickname)
            ? partnerSpecies
            : ruleEvent.Nickname!;

        var linkedSpecies = ruleEvent.LinkedSpeciesId is > 0
            ? ResolveSpeciesName(ruleEvent.LinkedSpeciesId.Value, ruleEvent.LinkedSpeciesName)
            : string.IsNullOrWhiteSpace(ruleEvent.LinkedSpeciesName)
                ? "verknüpftes Pokémon"
                : ruleEvent.LinkedSpeciesName!;
        var linkedName = string.IsNullOrWhiteSpace(ruleEvent.LinkedNickname)
            ? linkedSpecies
            : ruleEvent.LinkedNickname!;

        return $"Partner K.O. - {partnerName} K.O., {linkedName} raus!";
    }

    private string FormatPartnerBoxed(NuzlockeRuleEvent ruleEvent)
    {
        var partnerSpecies = ResolveSpeciesName(ruleEvent.SpeciesId, ruleEvent.SpeciesName);
        var partnerName = string.IsNullOrWhiteSpace(ruleEvent.Nickname)
            ? partnerSpecies
            : ruleEvent.Nickname!;

        var linkedSpecies = ruleEvent.LinkedSpeciesId is > 0
            ? ResolveSpeciesName(ruleEvent.LinkedSpeciesId.Value, ruleEvent.LinkedSpeciesName)
            : string.IsNullOrWhiteSpace(ruleEvent.LinkedSpeciesName)
                ? "verknüpftes Pokémon"
                : ruleEvent.LinkedSpeciesName!;
        var linkedName = string.IsNullOrWhiteSpace(ruleEvent.LinkedNickname)
            ? linkedSpecies
            : ruleEvent.LinkedNickname!;

        return $"Partner hat {partnerName} in die Box gelegt! (SoulLink: {linkedName})";
    }

    private string ResolveSpeciesName(int speciesId, string? fallback)
    {
        if (_speciesNames.TryGetValue(speciesId, out var resolved))
            return resolved;

        return string.IsNullOrWhiteSpace(fallback)
            ? $"Pokémon #{speciesId}"
            : fallback!;
    }

    private static string NormalizePartnerLocation(string? location)
    {
        var value = (location ?? string.Empty).Trim();
        return value switch
        {
            "Finsterhöhle" or "Dark Cave" or "Placeholder 1" => "Dunkelhöhle",
            "Sprout Tower" or "Placeholder 2" or "" => "Knofensaturm",
            _ => value
        };
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
            return new Dictionary<int, string>();
        }
    }

    private sealed class OverlayMessage
    {
        public string Message { get; init; } = string.Empty;
        public int DurationSeconds { get; init; }
    }
}
