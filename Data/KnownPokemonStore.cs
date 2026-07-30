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
    private readonly HashSet<string> _soullockeSyncedPokemonIds = [];

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

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);
        await CreateTableAsync(connection, cancellationToken);
        await EnsureSoullockeSyncedColumnAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT unique_id, soullocke_synced
            FROM known_pokemon;
            """;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            _knownPokemonIds.Add(id);

            if (reader.GetInt32(1) != 0)
            {
                _soullockeSyncedPokemonIds.Add(id);
            }
        }
    }

    public bool Contains(string id)
    {
        return _knownPokemonIds.Contains(id);
    }

    public bool IsSoullockeSynced(string id)
    {
        return _soullockeSyncedPokemonIds.Contains(id);
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
        await CreateTableAsync(connection, cancellationToken);
        await EnsureSoullockeSyncedColumnAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO known_pokemon
            (
                unique_id,
                species,
                nickname,
                location,
                location_id,
                first_seen_at,
                soullocke_synced
            )
            VALUES
            (
                $uniqueId,
                $species,
                $nickname,
                $location,
                $locationId,
                $firstSeenAt,
                0
            );
            """;

        command.Parameters.AddWithValue("$uniqueId", id);
        command.Parameters.AddWithValue("$species", entry.Species);
        command.Parameters.AddWithValue(
            "$nickname",
            entry.Nickname is null ? DBNull.Value : entry.Nickname);
        command.Parameters.AddWithValue("$location", entry.Location);
        command.Parameters.AddWithValue("$locationId", entry.LocationId);
        command.Parameters.AddWithValue(
            "$firstSeenAt",
            DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
        _knownPokemonIds.Add(id);
    }

    public async Task MarkSoullockeSyncedAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);
        await CreateTableAsync(connection, cancellationToken);
        await EnsureSoullockeSyncedColumnAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE known_pokemon
            SET soullocke_synced = 1
            WHERE unique_id = $uniqueId;
            """;
        command.Parameters.AddWithValue("$uniqueId", id);

        await command.ExecuteNonQueryAsync(cancellationToken);
        _soullockeSyncedPokemonIds.Add(id);
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
                first_seen_at TEXT NOT NULL,
                soullocke_synced INTEGER NOT NULL DEFAULT 0
            );
            """;

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    private static async Task EnsureSoullockeSyncedColumnAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using var checkCommand = connection.CreateCommand();
        checkCommand.CommandText = "PRAGMA table_info(known_pokemon);";

        var columnExists = false;

        await using (var reader =
                     await checkCommand.ExecuteReaderAsync(cancellationToken))
        {
            while (await reader.ReadAsync(cancellationToken))
            {
                if (string.Equals(
                        reader.GetString(1),
                        "soullocke_synced",
                        StringComparison.OrdinalIgnoreCase))
                {
                    columnExists = true;
                    break;
                }
            }
        }

        if (columnExists)
        {
            return;
        }

        await using var alterCommand = connection.CreateCommand();
        alterCommand.CommandText = """
            ALTER TABLE known_pokemon
            ADD COLUMN soullocke_synced INTEGER NOT NULL DEFAULT 0;
            """;

        await alterCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
