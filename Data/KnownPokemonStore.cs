using Microsoft.Data.Sqlite;

namespace SoulBuddy.Data;

public sealed class KnownPokemonEntry
{
    public string UniqueId { get; set; } = string.Empty;
    public int SpeciesId { get; set; }
    public string Species { get; set; } = string.Empty;
    public string? Nickname { get; set; }
    public long Pid { get; set; }
    public int OriginalTrainerId { get; set; }
    public int OriginalTrainerSecretId { get; set; }
    public string Location { get; set; } = string.Empty;
    public int LocationId { get; set; }
    public int LevelMet { get; set; }
    public int CurrentLevel { get; set; }
    public int CurrentHp { get; set; }
    public int MaxHp { get; set; }
    public bool IsEgg { get; set; }
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
    public bool SoullockeSynced { get; set; }
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
        _knownPokemonIds.Clear();
        _soullockeSyncedPokemonIds.Clear();

        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

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

        var now = DateTimeOffset.UtcNow;

        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO known_pokemon
            (
                unique_id,
                species_id,
                species,
                nickname,
                pid,
                original_trainer_id,
                original_trainer_secret_id,
                location,
                location_id,
                level_met,
                current_level,
                current_hp,
                max_hp,
                is_egg,
                first_seen_at,
                last_seen_at,
                soullocke_synced
            )
            VALUES
            (
                $uniqueId,
                $speciesId,
                $species,
                $nickname,
                $pid,
                $originalTrainerId,
                $originalTrainerSecretId,
                $location,
                $locationId,
                $levelMet,
                $currentLevel,
                $currentHp,
                $maxHp,
                $isEgg,
                $firstSeenAt,
                $lastSeenAt,
                0
            );
            """;

        command.Parameters.AddWithValue("$uniqueId", id);
        command.Parameters.AddWithValue("$speciesId", entry.SpeciesId);
        command.Parameters.AddWithValue("$species", entry.Species);
        command.Parameters.AddWithValue(
            "$nickname",
            entry.Nickname is null ? DBNull.Value : entry.Nickname);
        command.Parameters.AddWithValue("$pid", entry.Pid);
        command.Parameters.AddWithValue(
            "$originalTrainerId",
            entry.OriginalTrainerId);
        command.Parameters.AddWithValue(
            "$originalTrainerSecretId",
            entry.OriginalTrainerSecretId);
        command.Parameters.AddWithValue("$location", entry.Location);
        command.Parameters.AddWithValue("$locationId", entry.LocationId);
        command.Parameters.AddWithValue("$levelMet", entry.LevelMet);
        command.Parameters.AddWithValue("$currentLevel", entry.CurrentLevel);
        command.Parameters.AddWithValue("$currentHp", entry.CurrentHp);
        command.Parameters.AddWithValue("$maxHp", entry.MaxHp);
        command.Parameters.AddWithValue("$isEgg", entry.IsEgg ? 1 : 0);
        command.Parameters.AddWithValue("$firstSeenAt", now.ToString("O"));
        command.Parameters.AddWithValue("$lastSeenAt", now.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
        _knownPokemonIds.Add(id);
    }

    public async Task UpdateCurrentStateAsync(
        string id,
        int currentLevel,
        int currentHp,
        int maxHp,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE known_pokemon
            SET current_level = $currentLevel,
                current_hp = $currentHp,
                max_hp = $maxHp,
                last_seen_at = $lastSeenAt
            WHERE unique_id = $uniqueId
              AND (
                  current_level <> $currentLevel OR
                  current_hp <> $currentHp OR
                  max_hp <> $maxHp
              );
            """;

        command.Parameters.AddWithValue("$uniqueId", id);
        command.Parameters.AddWithValue("$currentLevel", currentLevel);
        command.Parameters.AddWithValue("$currentHp", currentHp);
        command.Parameters.AddWithValue("$maxHp", maxHp);
        command.Parameters.AddWithValue(
            "$lastSeenAt",
            DateTimeOffset.UtcNow.ToString("O"));

        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<KnownPokemonEntry>> GetAllAsync(
        CancellationToken cancellationToken)
    {
        var result = new List<KnownPokemonEntry>();

        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT
                unique_id,
                species_id,
                species,
                nickname,
                pid,
                original_trainer_id,
                original_trainer_secret_id,
                location,
                location_id,
                level_met,
                current_level,
                current_hp,
                max_hp,
                is_egg,
                first_seen_at,
                last_seen_at,
                soullocke_synced
            FROM known_pokemon
            ORDER BY first_seen_at;
            """;

        await using var reader =
            await command.ExecuteReaderAsync(cancellationToken);

        while (await reader.ReadAsync(cancellationToken))
        {
            result.Add(new KnownPokemonEntry
            {
                UniqueId = reader.GetString(0),
                SpeciesId = reader.GetInt32(1),
                Species = reader.GetString(2),
                Nickname = reader.IsDBNull(3) ? null : reader.GetString(3),
                Pid = reader.GetInt64(4),
                OriginalTrainerId = reader.GetInt32(5),
                OriginalTrainerSecretId = reader.GetInt32(6),
                Location = reader.GetString(7),
                LocationId = reader.GetInt32(8),
                LevelMet = reader.GetInt32(9),
                CurrentLevel = reader.GetInt32(10),
                CurrentHp = reader.GetInt32(11),
                MaxHp = reader.GetInt32(12),
                IsEgg = reader.GetInt32(13) != 0,
                FirstSeenAt = DateTimeOffset.Parse(reader.GetString(14)),
                LastSeenAt = DateTimeOffset.Parse(reader.GetString(15)),
                SoullockeSynced = reader.GetInt32(16) != 0
            });
        }

        return result;
    }

    public async Task MarkSoullockeSyncedAsync(
        string id,
        CancellationToken cancellationToken)
    {
        await using var connection =
            new SqliteConnection(_connectionString);

        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);

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

    private static async Task EnsureSchemaAsync(
        SqliteConnection connection,
        CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS known_pokemon
                (
                    unique_id TEXT PRIMARY KEY,
                    species_id INTEGER NOT NULL DEFAULT 0,
                    species TEXT NOT NULL,
                    nickname TEXT NULL,
                    pid INTEGER NOT NULL DEFAULT 0,
                    original_trainer_id INTEGER NOT NULL DEFAULT 0,
                    original_trainer_secret_id INTEGER NOT NULL DEFAULT 0,
                    location TEXT NOT NULL,
                    location_id INTEGER NOT NULL,
                    level_met INTEGER NOT NULL DEFAULT 0,
                    current_level INTEGER NOT NULL DEFAULT 0,
                    current_hp INTEGER NOT NULL DEFAULT 0,
                    max_hp INTEGER NOT NULL DEFAULT 0,
                    is_egg INTEGER NOT NULL DEFAULT 0,
                    first_seen_at TEXT NOT NULL,
                    last_seen_at TEXT NOT NULL DEFAULT '',
                    soullocke_synced INTEGER NOT NULL DEFAULT 0
                );
                """;

            await command.ExecuteNonQueryAsync(cancellationToken);
        }

        var requiredColumns = new Dictionary<string, string>(
            StringComparer.OrdinalIgnoreCase)
        {
            ["species_id"] = "INTEGER NOT NULL DEFAULT 0",
            ["pid"] = "INTEGER NOT NULL DEFAULT 0",
            ["original_trainer_id"] = "INTEGER NOT NULL DEFAULT 0",
            ["original_trainer_secret_id"] = "INTEGER NOT NULL DEFAULT 0",
            ["level_met"] = "INTEGER NOT NULL DEFAULT 0",
            ["current_level"] = "INTEGER NOT NULL DEFAULT 0",
            ["current_hp"] = "INTEGER NOT NULL DEFAULT 0",
            ["max_hp"] = "INTEGER NOT NULL DEFAULT 0",
            ["is_egg"] = "INTEGER NOT NULL DEFAULT 0",
            ["last_seen_at"] = "TEXT NOT NULL DEFAULT ''",
            ["soullocke_synced"] = "INTEGER NOT NULL DEFAULT 0"
        };

        var existingColumns = new HashSet<string>(
            StringComparer.OrdinalIgnoreCase);

        await using (var checkCommand = connection.CreateCommand())
        {
            checkCommand.CommandText = "PRAGMA table_info(known_pokemon);";

            await using var reader =
                await checkCommand.ExecuteReaderAsync(cancellationToken);

            while (await reader.ReadAsync(cancellationToken))
            {
                existingColumns.Add(reader.GetString(1));
            }
        }

        foreach (var column in requiredColumns)
        {
            if (existingColumns.Contains(column.Key))
            {
                continue;
            }

            await using var alterCommand = connection.CreateCommand();
            alterCommand.CommandText =
                $"ALTER TABLE known_pokemon ADD COLUMN {column.Key} {column.Value};";

            await alterCommand.ExecuteNonQueryAsync(cancellationToken);
        }

        await using var migrationCommand = connection.CreateCommand();
        migrationCommand.CommandText = """
            UPDATE known_pokemon
            SET last_seen_at = first_seen_at
            WHERE last_seen_at = '';
            """;

        await migrationCommand.ExecuteNonQueryAsync(cancellationToken);
    }
}
