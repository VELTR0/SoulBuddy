using System.Text.Json;
using SoulBuddy.Models;

namespace SoulBuddy.Sources;

public sealed class JsonPartySource : IPartySource
{
    private readonly string _filePath;

    private readonly JsonSerializerOptions _jsonOptions =
        new()
        {
            PropertyNameCaseInsensitive = true
        };

    public JsonPartySource(string filePath)
    {
        _filePath = filePath;
    }

    public async Task<IReadOnlyList<PartySlot>> ReadPartyAsync(
        CancellationToken cancellationToken)
    {
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                if (!File.Exists(_filePath))
                {
                    return [];
                }

                await using var stream = new FileStream(
                    _filePath,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);

                var party = await JsonSerializer.DeserializeAsync<List<PartySlot>>(
                    stream,
                    _jsonOptions,
                    cancellationToken);

                return party ?? [];
            }
            catch (JsonException) when (attempt < 5)
            {
                await Task.Delay(100, cancellationToken);
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(100, cancellationToken);
            }
        }

        return [];
    }
}