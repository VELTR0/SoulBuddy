using System.Text.Json;

namespace SoulSync.Data;

public sealed class KnownPokemonStore
{
    private readonly string _path;
    private readonly HashSet<string> _knownIds = [];

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

        var ids = await JsonSerializer.DeserializeAsync<HashSet<string>>(
            stream,
            cancellationToken: cancellationToken);

        if (ids is null)
        {
            return;
        }

        foreach (var id in ids)
        {
            _knownIds.Add(id);
        }
    }

    public bool Contains(string id) => _knownIds.Contains(id);

    public async Task AddAsync(
        string id,
        CancellationToken cancellationToken)
    {
        if (!_knownIds.Add(id))
        {
            return;
        }

        await SaveAsync(cancellationToken);
    }

    private async Task SaveAsync(CancellationToken cancellationToken)
    {
        await using var stream = File.Create(_path);

        await JsonSerializer.SerializeAsync(
            stream,
            _knownIds,
            new JsonSerializerOptions { WriteIndented = true },
            cancellationToken);
    }
}