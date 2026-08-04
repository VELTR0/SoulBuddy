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
    public string EncounterStatus { get; set; } = "alive";
}

public sealed class KnownPokemonStore
{
    private readonly string _connectionString;
    private readonly HashSet<string> _knownPokemonIds = [];
    private readonly HashSet<string> _soullockeSyncedPokemonIds = [];

    public KnownPokemonStore(string databasePath)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(databasePath) ?? ".");
        _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();
    }

    public async Task LoadAsync(CancellationToken cancellationToken)
    {
        _knownPokemonIds.Clear();
        _soullockeSyncedPokemonIds.Clear();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT unique_id, soullocke_synced FROM known_pokemon;";
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var id = reader.GetString(0);
            _knownPokemonIds.Add(id);
            if (reader.GetInt32(1) != 0) _soullockeSyncedPokemonIds.Add(id);
        }
    }

    public bool Contains(string id) => _knownPokemonIds.Contains(id);
    public bool IsSoullockeSynced(string id) => _soullockeSyncedPokemonIds.Contains(id);

    public async Task AddAsync(string id, KnownPokemonEntry entry, CancellationToken cancellationToken)
    {
        if (_knownPokemonIds.Contains(id)) return;
        var now = DateTimeOffset.UtcNow;
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            INSERT OR IGNORE INTO known_pokemon
            (unique_id,species_id,species,nickname,pid,original_trainer_id,original_trainer_secret_id,
             location,location_id,level_met,current_level,current_hp,max_hp,is_egg,first_seen_at,last_seen_at,
             soullocke_synced,encounter_status)
            VALUES
            ($id,$speciesId,$species,$nickname,$pid,$otId,$otSecret,$location,$locationId,$levelMet,
             $level,$hp,$maxHp,$isEgg,$firstSeen,$lastSeen,0,$status);
            """;
        AddEntryParameters(command, id, entry, now);
        await command.ExecuteNonQueryAsync(cancellationToken);
        _knownPokemonIds.Add(id);
    }

    public async Task<KnownPokemonEntry?> FindByLocationAsync(string location, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT unique_id,species_id,species,nickname,pid,original_trainer_id,original_trainer_secret_id,
                   location,location_id,level_met,current_level,current_hp,max_hp,is_egg,first_seen_at,last_seen_at,
                   soullocke_synced,encounter_status
            FROM known_pokemon WHERE lower(trim(location))=lower(trim($location)) LIMIT 1;
            """;
        command.Parameters.AddWithValue("$location", location);
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        return await reader.ReadAsync(cancellationToken) ? ReadEntry(reader) : null;
    }

    public async Task UpsertSoullockeEncounterAsync(
        string id, int speciesId, string? nickname, string location, string status,
        CancellationToken cancellationToken)
    {
        var existing = await FindByLocationAsync(location, cancellationToken);
        var targetId = existing?.UniqueId ?? id;
        if (existing is null)
        {
            await AddAsync(targetId, new KnownPokemonEntry
            {
                UniqueId = targetId,
                SpeciesId = speciesId,
                Species = $"Pokémon #{speciesId}",
                Nickname = nickname,
                Location = location,
                CurrentHp = status == "fainted" ? 0 : 1,
                MaxHp = 1,
                EncounterStatus = status
            }, cancellationToken);
        }

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE known_pokemon SET species_id=$speciesId,
                nickname=COALESCE($nickname,nickname), encounter_status=$status,
                current_hp=CASE WHEN $status='fainted' THEN 0 ELSE current_hp END,
                max_hp=CASE WHEN max_hp=0 THEN 1 ELSE max_hp END,
                soullocke_synced=1,last_seen_at=$now WHERE unique_id=$id;
            """;
        command.Parameters.AddWithValue("$id", targetId);
        command.Parameters.AddWithValue("$speciesId", speciesId);
        command.Parameters.AddWithValue("$nickname", nickname is null ? DBNull.Value : nickname);
        command.Parameters.AddWithValue("$status", NormalizeStatus(status));
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        _knownPokemonIds.Add(targetId);
        _soullockeSyncedPokemonIds.Add(targetId);
    }

    public async Task<string> MergeGamePokemonAsync(
        string gameId, KnownPokemonEntry gameEntry, bool inBox, CancellationToken cancellationToken)
    {
        var byLocation = await FindByLocationAsync(gameEntry.Location, cancellationToken);
        var targetId = byLocation?.UniqueId ?? gameId;
        if (byLocation is null && !Contains(gameId))
            await AddAsync(gameId, gameEntry, cancellationToken);

        var previousStatus = byLocation?.EncounterStatus ?? "alive";
        var nextStatus = gameEntry.CurrentHp <= 0 ? "fainted" : inBox ? "boxed" : "alive";
        // Explicit Soullocke failure states stay authoritative until a real caught Pokémon appears.
        if ((previousStatus is "notcaught" or "brofailed") && gameEntry.Pid == 0)
            nextStatus = previousStatus;

        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            UPDATE known_pokemon SET species_id=$speciesId,species=$species,nickname=$nickname,pid=$pid,
                original_trainer_id=$otId,original_trainer_secret_id=$otSecret,location_id=$locationId,
                level_met=$levelMet,current_level=$level,current_hp=$hp,max_hp=$maxHp,is_egg=$isEgg,
                encounter_status=$status,last_seen_at=$now WHERE unique_id=$id;
            """;
        command.Parameters.AddWithValue("$id", targetId);
        command.Parameters.AddWithValue("$speciesId", gameEntry.SpeciesId);
        command.Parameters.AddWithValue("$species", gameEntry.Species);
        command.Parameters.AddWithValue("$nickname", gameEntry.Nickname is null ? DBNull.Value : gameEntry.Nickname);
        command.Parameters.AddWithValue("$pid", gameEntry.Pid);
        command.Parameters.AddWithValue("$otId", gameEntry.OriginalTrainerId);
        command.Parameters.AddWithValue("$otSecret", gameEntry.OriginalTrainerSecretId);
        command.Parameters.AddWithValue("$locationId", gameEntry.LocationId);
        command.Parameters.AddWithValue("$levelMet", gameEntry.LevelMet);
        command.Parameters.AddWithValue("$level", gameEntry.CurrentLevel);
        command.Parameters.AddWithValue("$hp", gameEntry.CurrentHp);
        command.Parameters.AddWithValue("$maxHp", gameEntry.MaxHp);
        command.Parameters.AddWithValue("$isEgg", gameEntry.IsEgg ? 1 : 0);
        command.Parameters.AddWithValue("$status", nextStatus);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
        return targetId;
    }

    public async Task UpdateCurrentStateAsync(string id, int level, int hp, int maxHp, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE known_pokemon SET current_level=$level,current_hp=$hp,max_hp=$maxHp,last_seen_at=$now WHERE unique_id=$id;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$level", level);
        command.Parameters.AddWithValue("$hp", hp);
        command.Parameters.AddWithValue("$maxHp", maxHp);
        command.Parameters.AddWithValue("$now", DateTimeOffset.UtcNow.ToString("O"));
        await command.ExecuteNonQueryAsync(cancellationToken);
    }

    public async Task<IReadOnlyList<KnownPokemonEntry>> GetAllAsync(CancellationToken cancellationToken)
    {
        var result = new List<KnownPokemonEntry>();
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT unique_id,species_id,species,nickname,pid,original_trainer_id,original_trainer_secret_id,
                   location,location_id,level_met,current_level,current_hp,max_hp,is_egg,first_seen_at,last_seen_at,
                   soullocke_synced,encounter_status FROM known_pokemon ORDER BY first_seen_at;
            """;
        await using var reader = await command.ExecuteReaderAsync(cancellationToken);
        while (await reader.ReadAsync(cancellationToken))
        {
            var entry = ReadEntry(reader);
            // Existing UI displays Location for unsynced entries. Return a presentation copy with status appended.
            entry.Location = $"{entry.Location} · {StatusDisplay(entry.EncounterStatus)}";
            entry.SoullockeSynced = false;
            result.Add(entry);
        }
        return result;
    }

    public async Task MarkSoullockeSyncedAsync(string id, CancellationToken cancellationToken)
    {
        await using var connection = new SqliteConnection(_connectionString);
        await connection.OpenAsync(cancellationToken);
        await EnsureSchemaAsync(connection, cancellationToken);
        await using var command = connection.CreateCommand();
        command.CommandText = "UPDATE known_pokemon SET soullocke_synced=1 WHERE unique_id=$id;";
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken);
        _soullockeSyncedPokemonIds.Add(id);
    }

    private static string NormalizeStatus(string? status) => (status ?? "alive").Trim().ToLowerInvariant() switch
    {
        "fainted" => "fainted", "notcaught" => "notcaught", "brofailed" => "brofailed",
        "boxed" => "boxed", _ => "alive"
    };

    public static string StatusDisplay(string status) => NormalizeStatus(status) switch
    {
        "fainted" => "Besiegt", "notcaught" => "Nicht gefangen", "brofailed" => "Bro-Failed",
        "boxed" => "Box", _ => "Lebendig"
    };

    private static void AddEntryParameters(SqliteCommand command, string id, KnownPokemonEntry e, DateTimeOffset now)
    {
        command.Parameters.AddWithValue("$id", id); command.Parameters.AddWithValue("$speciesId", e.SpeciesId);
        command.Parameters.AddWithValue("$species", e.Species); command.Parameters.AddWithValue("$nickname", e.Nickname is null ? DBNull.Value : e.Nickname);
        command.Parameters.AddWithValue("$pid", e.Pid); command.Parameters.AddWithValue("$otId", e.OriginalTrainerId);
        command.Parameters.AddWithValue("$otSecret", e.OriginalTrainerSecretId); command.Parameters.AddWithValue("$location", e.Location);
        command.Parameters.AddWithValue("$locationId", e.LocationId); command.Parameters.AddWithValue("$levelMet", e.LevelMet);
        command.Parameters.AddWithValue("$level", e.CurrentLevel); command.Parameters.AddWithValue("$hp", e.CurrentHp);
        command.Parameters.AddWithValue("$maxHp", e.MaxHp); command.Parameters.AddWithValue("$isEgg", e.IsEgg ? 1 : 0);
        command.Parameters.AddWithValue("$firstSeen", now.ToString("O")); command.Parameters.AddWithValue("$lastSeen", now.ToString("O"));
        command.Parameters.AddWithValue("$status", NormalizeStatus(e.EncounterStatus));
    }

    private static KnownPokemonEntry ReadEntry(SqliteDataReader r) => new()
    {
        UniqueId=r.GetString(0),SpeciesId=r.GetInt32(1),Species=r.GetString(2),Nickname=r.IsDBNull(3)?null:r.GetString(3),
        Pid=r.GetInt64(4),OriginalTrainerId=r.GetInt32(5),OriginalTrainerSecretId=r.GetInt32(6),Location=r.GetString(7),
        LocationId=r.GetInt32(8),LevelMet=r.GetInt32(9),CurrentLevel=r.GetInt32(10),CurrentHp=r.GetInt32(11),MaxHp=r.GetInt32(12),
        IsEgg=r.GetInt32(13)!=0,FirstSeenAt=DateTimeOffset.Parse(r.GetString(14)),LastSeenAt=DateTimeOffset.Parse(r.GetString(15)),
        SoullockeSynced=r.GetInt32(16)!=0,EncounterStatus=NormalizeStatus(r.GetString(17))
    };

    private static async Task EnsureSchemaAsync(SqliteConnection connection, CancellationToken cancellationToken)
    {
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS known_pokemon(
                unique_id TEXT PRIMARY KEY,species_id INTEGER NOT NULL DEFAULT 0,species TEXT NOT NULL,nickname TEXT NULL,
                pid INTEGER NOT NULL DEFAULT 0,original_trainer_id INTEGER NOT NULL DEFAULT 0,original_trainer_secret_id INTEGER NOT NULL DEFAULT 0,
                location TEXT NOT NULL,location_id INTEGER NOT NULL DEFAULT 0,level_met INTEGER NOT NULL DEFAULT 0,current_level INTEGER NOT NULL DEFAULT 0,
                current_hp INTEGER NOT NULL DEFAULT 0,max_hp INTEGER NOT NULL DEFAULT 0,is_egg INTEGER NOT NULL DEFAULT 0,first_seen_at TEXT NOT NULL,
                last_seen_at TEXT NOT NULL DEFAULT '',soullocke_synced INTEGER NOT NULL DEFAULT 0,encounter_status TEXT NOT NULL DEFAULT 'alive');
                """;
            await command.ExecuteNonQueryAsync(cancellationToken);
        }
        var columns = new Dictionary<string,string>{{"species_id","INTEGER NOT NULL DEFAULT 0"},{"pid","INTEGER NOT NULL DEFAULT 0"},
            {"original_trainer_id","INTEGER NOT NULL DEFAULT 0"},{"original_trainer_secret_id","INTEGER NOT NULL DEFAULT 0"},
            {"level_met","INTEGER NOT NULL DEFAULT 0"},{"current_level","INTEGER NOT NULL DEFAULT 0"},{"current_hp","INTEGER NOT NULL DEFAULT 0"},
            {"max_hp","INTEGER NOT NULL DEFAULT 0"},{"is_egg","INTEGER NOT NULL DEFAULT 0"},{"last_seen_at","TEXT NOT NULL DEFAULT ''"},
            {"soullocke_synced","INTEGER NOT NULL DEFAULT 0"},{"encounter_status","TEXT NOT NULL DEFAULT 'alive'"}};
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using (var check = connection.CreateCommand())
        {
            check.CommandText="PRAGMA table_info(known_pokemon);";
            await using var reader=await check.ExecuteReaderAsync(cancellationToken);
            while(await reader.ReadAsync(cancellationToken)) existing.Add(reader.GetString(1));
        }
        foreach(var column in columns.Where(c=>!existing.Contains(c.Key)))
        {
            await using var alter=connection.CreateCommand();
            alter.CommandText=$"ALTER TABLE known_pokemon ADD COLUMN {column.Key} {column.Value};";
            await alter.ExecuteNonQueryAsync(cancellationToken);
        }
        await using var migration=connection.CreateCommand();
        migration.CommandText="UPDATE known_pokemon SET last_seen_at=first_seen_at WHERE last_seen_at='';";
        await migration.ExecuteNonQueryAsync(cancellationToken);
    }
}
