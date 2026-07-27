using System.Text.Json;
using SoulSync.Models;

namespace SoulSync.Services;

public sealed class PartyReader
{
    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    public async Task<IReadOnlyList<PartySlot>> ReadAsync(
        string path,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path))
        {
            throw new FileNotFoundException(
                "Die party.json wurde nicht gefunden.",
                path);
        }

        // Das Lua-Skript kann die Datei genau in diesem Moment überschreiben.
        // Deshalb versuchen wir das Lesen bei einem temporären Fehler erneut.
        for (var attempt = 1; attempt <= 5; attempt++)
        {
            try
            {
                await using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite);

                var party = await JsonSerializer.DeserializeAsync<List<PartySlot>>(
                    stream,
                    JsonOptions,
                    cancellationToken);

                return party ?? [];
            }
            catch (JsonException) when (attempt < 5)
            {
                await Task.Delay(150, cancellationToken);
            }
            catch (IOException) when (attempt < 5)
            {
                await Task.Delay(150, cancellationToken);
            }
        }

        throw new InvalidOperationException(
            "party.json konnte nach mehreren Versuchen nicht gelesen werden.");
    }
}