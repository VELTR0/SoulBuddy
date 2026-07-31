using System.Collections.Concurrent;
using System.Text.Json;
using Avalonia.Media.Imaging;

namespace SoulBuddy.Services;

public sealed record PokemonVisualData(
    Bitmap? Sprite,
    IReadOnlyList<string> Types);

public sealed class PokemonVisualService : IDisposable
{
    private readonly HttpClient _httpClient = new()
    {
        Timeout = TimeSpan.FromSeconds(8)
    };

    private readonly ConcurrentDictionary<int, Task<PokemonVisualData>> _cache = new();

    public Task<PokemonVisualData> GetAsync(
        int speciesId,
        bool isShiny,
        CancellationToken cancellationToken = default)
    {
        if (speciesId <= 0)
        {
            return Task.FromResult(new PokemonVisualData(null, []));
        }

        var cacheKey = isShiny ? -speciesId : speciesId;
        return _cache.GetOrAdd(
            cacheKey,
            _ => LoadAsync(speciesId, isShiny, cancellationToken));
    }

    private async Task<PokemonVisualData> LoadAsync(
        int speciesId,
        bool isShiny,
        CancellationToken cancellationToken)
    {
        try
        {
            var apiUrl = $"https://pokeapi.co/api/v2/pokemon/{speciesId}";
            await using var apiStream = await _httpClient.GetStreamAsync(
                apiUrl,
                cancellationToken);
            using var document = await JsonDocument.ParseAsync(
                apiStream,
                cancellationToken: cancellationToken);

            var root = document.RootElement;
            var typeNames = root.GetProperty("types")
                .EnumerateArray()
                .OrderBy(item => item.GetProperty("slot").GetInt32())
                .Select(item => item.GetProperty("type").GetProperty("name").GetString())
                .Where(name => !string.IsNullOrWhiteSpace(name))
                .Select(name => TranslateType(name!))
                .ToArray();

            var sprites = root.GetProperty("sprites");
            var spriteProperty = isShiny ? "front_shiny" : "front_default";
            var spriteUrl = sprites.GetProperty(spriteProperty).GetString();

            if (string.IsNullOrWhiteSpace(spriteUrl))
            {
                return new PokemonVisualData(null, typeNames);
            }

            var spriteBytes = await _httpClient.GetByteArrayAsync(
                spriteUrl,
                cancellationToken);
            await using var spriteStream = new MemoryStream(spriteBytes);
            var bitmap = new Bitmap(spriteStream);

            return new PokemonVisualData(bitmap, typeNames);
        }
        catch
        {
            return new PokemonVisualData(null, []);
        }
    }

    private static string TranslateType(string type)
    {
        return type switch
        {
            "normal" => "Normal",
            "fire" => "Feuer",
            "water" => "Wasser",
            "electric" => "Elektro",
            "grass" => "Pflanze",
            "ice" => "Eis",
            "fighting" => "Kampf",
            "poison" => "Gift",
            "ground" => "Boden",
            "flying" => "Flug",
            "psychic" => "Psycho",
            "bug" => "Käfer",
            "rock" => "Gestein",
            "ghost" => "Geist",
            "dragon" => "Drache",
            "dark" => "Unlicht",
            "steel" => "Stahl",
            "fairy" => "Fee",
            _ => type
        };
    }

    public void Dispose()
    {
        foreach (var task in _cache.Values)
        {
            if (task.IsCompletedSuccessfully)
            {
                task.Result.Sprite?.Dispose();
            }
        }

        _httpClient.Dispose();
    }
}