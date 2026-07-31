using System.Text.Json;
using SoulBuddy.Models;

namespace SoulBuddy.Services;

internal sealed class SoulLinkRegistry
{
    private readonly string _filePath;
    private readonly JsonSerializerOptions _jsonOptions = new()
    {
        WriteIndented = true
    };
    private string _lastSignature = string.Empty;

    public SoulLinkRegistry()
    {
        var runtimeDirectory = Path.Combine(AppContext.BaseDirectory, "runtime");
        Directory.CreateDirectory(runtimeDirectory);
        _filePath = Path.Combine(runtimeDirectory, "soullinks.json");
    }

    public IReadOnlyList<SoulLinkPair> Current { get; private set; } = [];

    public async Task UpdateAsync(IReadOnlyList<SoulLinkPair> pairs)
    {
        var signature = string.Join("|", pairs.Select(pair =>
            $"{pair.LocationKey}:{pair.LocalSpeciesId}:{pair.LocalCurrentHp}:" +
            $"{pair.PartnerSpeciesId}:{pair.PartnerCurrentHp}:{pair.Status}"));

        if (signature == _lastSignature)
        {
            return;
        }

        _lastSignature = signature;
        Current = pairs.ToArray();

        var json = JsonSerializer.Serialize(Current, _jsonOptions);
        await File.WriteAllTextAsync(_filePath, json);
    }
}
