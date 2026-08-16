using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;

namespace SoulBuddy.Services;

/// <summary>
/// Resolves tracker Pokémon slugs without adding another online dependency.
/// The collector already ships a National-Dex name-to-id table, so tracker
/// adapters reuse that single source of truth.
/// </summary>
internal sealed class PokemonSpeciesCatalog
{
    private static readonly Regex MappingLine = new(
        "\\[\\\"(?<name>[^\\\"]+)\\\"\\]\\s*=\\s*(?<id>\\d+)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private readonly Dictionary<string, int> _idByNormalizedName =
        new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<int, string> _slugById = [];

    public PokemonSpeciesCatalog()
    {
        var path = FindMappingPath()
            ?? throw new FileNotFoundException(
                "Die lokale Pokémon-Namensliste wurde nicht gefunden. " +
                "Bitte stelle sicher, dass der collectors-Ordner mit SoulBuddy ausgeliefert wird.");

        foreach (var line in File.ReadLines(path))
        {
            var match = MappingLine.Match(line);
            if (!match.Success ||
                !int.TryParse(match.Groups["id"].Value, out var id) ||
                id <= 0)
            {
                continue;
            }

            var name = match.Groups["name"].Value;
            _idByNormalizedName[NormalizeName(name)] = id;
            _slugById[id] = ToTrackerSlug(name);
        }

        if (_slugById.Count == 0)
            throw new InvalidOperationException("Die lokale Pokémon-Namensliste ist leer oder ungültig.");
    }

    public int ResolveId(string trackerName)
    {
        if (string.IsNullOrWhiteSpace(trackerName))
            return 0;

        return _idByNormalizedName.TryGetValue(NormalizeName(trackerName), out var id)
            ? id
            : 0;
    }

    public string ResolveSlug(int speciesId)
    {
        if (_slugById.TryGetValue(speciesId, out var slug))
            return slug;

        throw new InvalidOperationException(
            $"Pokémon #{speciesId} konnte nicht in einen Tracker-Namen umgewandelt werden.");
    }

    private static string NormalizeName(string value)
    {
        var prepared = value.Trim().ToLowerInvariant()
            .Replace("♀", "f", StringComparison.Ordinal)
            .Replace("♂", "m", StringComparison.Ordinal);

        var decomposed = prepared.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;
            if (char.IsLetterOrDigit(character))
                builder.Append(character);
        }

        return builder.ToString();
    }

    private static string ToTrackerSlug(string name)
    {
        if (string.Equals(name, "Nidoran♀", StringComparison.Ordinal))
            return "nidoran-f";
        if (string.Equals(name, "Nidoran♂", StringComparison.Ordinal))
            return "nidoran-m";

        var prepared = name.Trim().ToLowerInvariant()
            .Replace("’", string.Empty, StringComparison.Ordinal)
            .Replace("'", string.Empty, StringComparison.Ordinal)
            .Replace(".", string.Empty, StringComparison.Ordinal);

        var decomposed = prepared.Normalize(NormalizationForm.FormD);
        var builder = new StringBuilder(decomposed.Length);
        var pendingDash = false;
        foreach (var character in decomposed)
        {
            if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                continue;

            if (char.IsLetterOrDigit(character))
            {
                if (pendingDash && builder.Length > 0)
                    builder.Append('-');
                builder.Append(character);
                pendingDash = false;
            }
            else
            {
                pendingDash = true;
            }
        }

        return builder.ToString().Trim('-');
    }

    private static string? FindMappingPath()
    {
        foreach (var root in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory }
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var directory = new DirectoryInfo(Path.GetFullPath(root));
            while (directory is not null)
            {
                var candidate = Path.Combine(
                    directory.FullName,
                    "collectors",
                    "desmume-gen4",
                    "pokemon_name_to_pokedex_id.lua");
                if (File.Exists(candidate))
                    return candidate;
                directory = directory.Parent;
            }
        }

        return null;
    }
}
