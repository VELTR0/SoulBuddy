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
        // partner additions and explicit box-status transitions are surfaced.
        SoullockePartnerCatchObserver.ResetAndSetHandler(WritePartnerCatch);
        SoullockePartnerCatchObserver.SetBoxHandler(WritePartnerBox);
    }

    public void Write(NuzlockeRuleEvent ruleEvent)
    {
        // Boxing your own Pokémon is still a rule event because SyncService needs it
        // to persist the local Soullocke status as "boxed". Only the local overlay is
        // suppressed; the partner still receives the remote boxing notification.
        if (ruleEvent.Type == NuzlockeRuleEventType.PokemonBoxed)
            return;

        WriteMessage(FormatMessage(ruleEvent));
    }

    private void WritePartnerCatch(SoullockePartnerCatchDetected partnerCatch)
    {
        var species = ResolveSpeciesName(partnerCatch.Pokemon, null);
        var name = string.IsNullOrWhiteSpace(partnerCatch.Nickname)
            ? species
            : partnerCatch.Nickname!;

        WriteMessage($"Partner hat {name} gefangen!");
        Console.WriteLine(
            $"SoulLink-Event: Partner hat {name} in {partnerCatch.Location} gefangen.");
    }

    private void WritePartnerBox(SoullockePartnerBoxDetected partnerBox)
    {
        var partnerSpecies = ResolveSpeciesName(partnerBox.Pokemon, null);
        var partnerName = string.IsNullOrWhiteSpace(partnerBox.Nickname)
            ? partnerSpecies
            : partnerBox.Nickname!;

        var linkedSpecies = partnerBox.LinkedPokemon is > 0
            ? ResolveSpeciesName(partnerBox.LinkedPokemon.Value, null)
            : "verknüpftes Pokémon";
        var linkedName = string.IsNullOrWhiteSpace(partnerBox.LinkedNickname)
            ? linkedSpecies
            : partnerBox.LinkedNickname!;

        WriteMessage($"{partnerName} boxed! (Linked: {linkedName})");
        Console.WriteLine(
            $"SoulLink-Event: {partnerName} wurde in {partnerBox.Location} eingeboxt; " +
            $"verknüpft mit {linkedName}.");
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

            // Partner boxing is also represented as a normal rule event in older
            // code paths. Suppress an identical message if both observers see it.
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

        return ruleEvent.Type switch
        {
            NuzlockeRuleEventType.PokemonKnockedOut =>
                $"{name} ist K.O.!",

            NuzlockeRuleEventType.PartnerPokemonKnockedOut =>
                FormatPartnerKnockout(ruleEvent),

            NuzlockeRuleEventType.PartnerPokemonBoxed =>
                FormatPartnerBoxed(ruleEvent),

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

        return $"{partnerName} boxed! (Linked:{linkedName})";
    }

    private string ResolveSpeciesName(int speciesId, string? fallback)
    {
        if (_speciesNames.TryGetValue(speciesId, out var resolved))
            return resolved;

        return string.IsNullOrWhiteSpace(fallback)
            ? $"Pokémon #{speciesId}"
            : fallback!;
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
