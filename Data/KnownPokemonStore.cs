using Microsoft.Data.Sqlite;

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
    private readonly string _connectionString;

    private readonly HashSet<string> _knownPokemonIds = [];

    public KnownPokemonStore(string databasePath)
    {
        var databaseDirectory = Path.GetDirectoryName(databasePath);

        if (!string.IsNullOrWhiteSpace(databaseDirectory))
        {
            Directory.CreateDirectory(databaseDirectory);
        }

        _connectionString = new SqliteConnectionStringBuilder
        {
            DataSource = databasePath
        }.ToString();
    }

    public async Task LoadAsync(
        CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await CreateTableAsync(
            connection,
            cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = """
            SELECT unique_id
            FROM known_pokemon;
            """;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            _knownPokemonIds.Add(
                reader.GetString(0));
        }
    }

    public bool Contains(string id)
    {
        return _knownPokemonIds.Contains(id);
    }

    public async Task AddAsync(
        string id,
        KnownPokemonEntry entry,
        CancellationToken cancellationToken)
    {
        if (_knownPokemonIds.Contains(id))
        {
            return;
        }

        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);

        await CreateTableAsync(
            connection,
            cancellationToken);

        await using var command = connection.CreateCommand();

        command.CommandText = """
            INSERT OR IGNORE INTO known_pokemon
            (
                unique_id,
                species,
                nickname,
                location,
                location_id,
                first_seen_at
            )
            VALUES
            (
                $uniqueId,
                $species,
                $nickname,
                $location,
                $locationId,
                $firstSeenAt
            );
            """;

        command.Parameters.AddWithValue(
            "$uniqueId",
            id);

        command.Parameters.AddWithValue(
            "$species",
            entry.Species);

        command.Parameters.AddWithValue(
            "$nickname",
            entry.Nickname is null
                ? DBNull.Value
                : entry.Nickname);

        command.Parameters.AddWithValue(
            "$location",
            entry.Location);

        command.Parameters.AddWithValue(
            "$locationId",
            entry.LocationId);

        command.Parameters.AddWithValue(
            "$firstSeenAt",
            DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(
            cancellationToken);

        _knownPokemonIds.Add(id);
    }

    private static async Task CreateTableAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var command = connection.CreateCommand();

        command.CommandText = """
            CREATE TABLE IF NOT EXISTS known_pokemon
            (
                unique_id TEXT PRIMARY KEY,
                species TEXT NOT NULL,
                nickname TEXT NULL,
                location TEXT NOT NULL,
                location_id INTEGER NOT NULL,
                first_seen_at TEXT NOT NULL
            );
            """;

        await command.ExecuteNonQueryAsync(
            cancellationToken);
    }
}