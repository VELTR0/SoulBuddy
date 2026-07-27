using System.Text.Json;

namespace SoulBuddy.Data;

public sealed class KnownPokemonEntry
{
    public string Species { get; set; } = string.Empty;
    public string? Nickname { get; set; }
    public string Location { get; set; } = string.Empty;
    public int LocationId { get; set; }
}

public sealed class KnownPokemonStore
{
    private readonly string _path;

    private readonly Dictionary<string, KnownPokemonEntry> _knownPokemon = [];

    public KnownPokemonStore(string path)
    {
        _path = path;
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        if (!File.Exists(_path))
        {
            return;
        }

        await using var stream = File.OpenRead(_path);

        var entries =
            await JsonSerializer.DeserializeAsync<
                Dictionary<string, KnownPokemonEntry>>(
                stream,
                cancellationToken: cancellationToken);

        if (entries is null)
        {
            return;
        }

        foreach (var entry in entries)
        {
            _knownPokemon[entry.Key] = entry.Value;
        }
    }

    public bool Contains(string id)
    {
        return _knownPokemon.ContainsKey(id);
    }

    public async Task AddAsync(
        string id,
        KnownPokemonEntry entry,
        CancellationToken cancellationToken)
    {
        if (_knownPokemon.ContainsKey(id))
        {
            return;
        }

        _knownPokemon[id] = entry;

        await SaveAsync(cancellationToken);
    }

    private async Task SaveAsync(
        CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_path);

        await JsonSerializer.SerializeAsync(
            stream,
            _knownPokemon,
            new JsonSerializerOptions
            {
                WriteIndented = true
            },
            cancellationToken);
    }
}