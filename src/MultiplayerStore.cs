using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

/// <summary>
/// 官方多人世界、地块分配和节点心跳。与排行榜账号库分开，删改不影响成绩。
/// 世界是否入榜固定为 0：官方服务器世界只做联机，不提交通关速度。
/// </summary>
public sealed class MultiplayerStore : IDisposable
{
    private readonly object _gate = new();
    private readonly SqliteConnection _db;

    public MultiplayerStore(string path)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        _db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, DefaultTimeout = 5 }.ToString());
        _db.Open();
        using var cmd = _db.CreateCommand();
        cmd.CommandText = """
            PRAGMA journal_mode=WAL;
            PRAGMA foreign_keys=ON;
            CREATE TABLE IF NOT EXISTS worlds (
              id TEXT PRIMARY KEY,name TEXT NOT NULL,map_id TEXT NOT NULL,status TEXT NOT NULL,
              capacity INTEGER NOT NULL,ranked INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS assignments (
              world_id TEXT NOT NULL,steam_id TEXT NOT NULL,plot_id TEXT NOT NULL,spawn_id TEXT NOT NULL,
              house_seed TEXT NOT NULL,house_x INTEGER NOT NULL,house_y INTEGER NOT NULL,created INTEGER NOT NULL,
              PRIMARY KEY(world_id,steam_id),UNIQUE(world_id,plot_id));
            CREATE TABLE IF NOT EXISTS nodes (
              id TEXT PRIMARY KEY,name TEXT NOT NULL,region TEXT NOT NULL,url TEXT NOT NULL,
              version TEXT NOT NULL,players INTEGER NOT NULL,last_seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS node_monitor_state (
              node_id TEXT PRIMARY KEY,world_id TEXT NOT NULL,last_seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS node_online (
              node_id TEXT NOT NULL,steam_id TEXT NOT NULL,PRIMARY KEY(node_id,steam_id));
            CREATE TABLE IF NOT EXISTS node_connections (
              id TEXT PRIMARY KEY,node_id TEXT NOT NULL,world_id TEXT NOT NULL,steam_id TEXT NOT NULL,
              type TEXT NOT NULL,at INTEGER NOT NULL,received INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS audit(id INTEGER PRIMARY KEY,action TEXT NOT NULL,target TEXT NOT NULL,at INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS world_reset_worker (world_id TEXT PRIMARY KEY,last_seen INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS world_reset_jobs (
              id TEXT PRIMARY KEY,world_id TEXT NOT NULL,phase TEXT NOT NULL,error TEXT NOT NULL DEFAULT '',
              created INTEGER NOT NULL,updated INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS world_start_authorizations (
              world_id TEXT PRIMARY KEY,job_id TEXT NOT NULL,node_id TEXT NOT NULL,
              generation INTEGER NOT NULL,boot_nonce TEXT NOT NULL,mode TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS world_bootstrap_jobs (
              id TEXT PRIMARY KEY,world_id TEXT NOT NULL,phase TEXT NOT NULL,created INTEGER NOT NULL,updated INTEGER NOT NULL);
            CREATE TABLE IF NOT EXISTS world_entry_readiness (
              world_id TEXT PRIMARY KEY,generation INTEGER NOT NULL,node_id TEXT NOT NULL,
              boot_nonce TEXT NOT NULL,last_seen INTEGER NOT NULL);
            """;
        cmd.ExecuteNonQuery();
        MigratePlotOwnership();
        EnsureColumn("world_reset_worker", "protocol_version", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("assignments", "map_generation", "INTEGER NOT NULL DEFAULT 0");
        using (var columns = Command("PRAGMA table_info(worlds)"))
        using (var reader = columns.ExecuteReader())
        {
            bool hasGeneration = false;
            while (reader.Read()) hasGeneration |= reader.GetString(1) == "generation";
            if (!hasGeneration)
            {
                reader.Close();
                using var migrate = Command("ALTER TABLE worlds ADD COLUMN generation INTEGER NOT NULL DEFAULT 1 CHECK(generation > 0)");
                migrate.ExecuteNonQuery();
            }
        }
        EnsureColumn("worlds", "boot_nonce", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("node_monitor_state", "generation", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("node_monitor_state", "boot_nonce", "TEXT NOT NULL DEFAULT ''");
        using (var trigger = Command("""
            CREATE TRIGGER IF NOT EXISTS worlds_generation_no_rollback
            BEFORE UPDATE OF generation ON worlds
            WHEN NEW.generation < OLD.generation OR NEW.generation < 1
            BEGIN SELECT RAISE(ABORT, 'world_generation_rollback'); END
            """)) trigger.ExecuteNonQuery();
        string worldName = Environment.GetEnvironmentVariable("WHEATSTEAD_WORLD_NAME")?.Trim()
            is { Length: > 0 } configured ? configured : "穗吟镇 1 号世界";
        using var seed = Command("""
            INSERT OR IGNORE INTO worlds(id,name,map_id,status,capacity,ranked)
            VALUES('official-1',$name,'town','open',8,0)
            """, ("$name", worldName));
        seed.ExecuteNonQuery();
    }

    // assignments is the player's admission record; ownership can change without
    // removing that record. Run this only once, otherwise a restart would restore
    // ownership to the original players after a capture.
    private void MigratePlotOwnership()
    {
        using var transaction = _db.BeginTransaction(deferred: false);
        using (var create = Command("""
            CREATE TABLE IF NOT EXISTS multiplayer_schema_migrations (name TEXT PRIMARY KEY);
            CREATE TABLE IF NOT EXISTS plot_owners (
              world_id TEXT NOT NULL,plot_id TEXT NOT NULL,steam_id TEXT NOT NULL,
              PRIMARY KEY(world_id,plot_id));
            CREATE TABLE IF NOT EXISTS plot_capture_events (
              world_id TEXT NOT NULL,generation INTEGER NOT NULL,event_id TEXT NOT NULL,
              plot_id TEXT NOT NULL,previous_owner TEXT NOT NULL,new_owner TEXT NOT NULL,
              PRIMARY KEY(world_id,generation,event_id));
            CREATE TABLE IF NOT EXISTS plot_houses (
              world_id TEXT NOT NULL,plot_id TEXT NOT NULL,house_id TEXT NOT NULL,
              house_version INTEGER NOT NULL,state TEXT NOT NULL CHECK(state IN ('intact','destroyed','claimed')),
              PRIMARY KEY(world_id,plot_id));
            CREATE TABLE IF NOT EXISTS plot_house_history (
              world_id TEXT NOT NULL,house_id TEXT NOT NULL,plot_id TEXT NOT NULL,house_version INTEGER NOT NULL,
              previous_house_id TEXT NOT NULL,
              PRIMARY KEY(world_id,house_id));
            CREATE TABLE IF NOT EXISTS official_world_maps (
              world_id TEXT PRIMARY KEY,generation INTEGER NOT NULL,map_json TEXT NOT NULL);
            CREATE TABLE IF NOT EXISTS plot_snapshot_revisions (
              world_id TEXT PRIMARY KEY,revision INTEGER NOT NULL CHECK(revision BETWEEN 1 AND 9007199254740991));
            CREATE TRIGGER IF NOT EXISTS plot_owners_snapshot_insert AFTER INSERT ON plot_owners BEGIN
              INSERT INTO plot_snapshot_revisions VALUES(NEW.world_id,2)
                ON CONFLICT(world_id) DO UPDATE SET revision=revision+1;
            END;
            CREATE TRIGGER IF NOT EXISTS plot_owners_snapshot_delete AFTER DELETE ON plot_owners BEGIN
              INSERT INTO plot_snapshot_revisions VALUES(OLD.world_id,2)
                ON CONFLICT(world_id) DO UPDATE SET revision=revision+1;
            END;
            CREATE TRIGGER IF NOT EXISTS plot_owners_snapshot_update AFTER UPDATE ON plot_owners BEGIN
              INSERT INTO plot_snapshot_revisions VALUES(OLD.world_id,2)
                ON CONFLICT(world_id) DO UPDATE SET revision=revision+1;
              INSERT INTO plot_snapshot_revisions SELECT NEW.world_id,2 WHERE NEW.world_id<>OLD.world_id
                ON CONFLICT(world_id) DO UPDATE SET revision=revision+1;
            END;
            CREATE TRIGGER IF NOT EXISTS plot_houses_snapshot_insert AFTER INSERT ON plot_houses BEGIN
              INSERT INTO plot_snapshot_revisions VALUES(NEW.world_id,2)
                ON CONFLICT(world_id) DO UPDATE SET revision=revision+1;
            END;
            CREATE TRIGGER IF NOT EXISTS plot_houses_snapshot_delete AFTER DELETE ON plot_houses BEGIN
              INSERT INTO plot_snapshot_revisions VALUES(OLD.world_id,2)
                ON CONFLICT(world_id) DO UPDATE SET revision=revision+1;
            END;
            CREATE TRIGGER IF NOT EXISTS plot_houses_snapshot_update AFTER UPDATE ON plot_houses BEGIN
              INSERT INTO plot_snapshot_revisions VALUES(OLD.world_id,2)
                ON CONFLICT(world_id) DO UPDATE SET revision=revision+1;
              INSERT INTO plot_snapshot_revisions SELECT NEW.world_id,2 WHERE NEW.world_id<>OLD.world_id
                ON CONFLICT(world_id) DO UPDATE SET revision=revision+1;
            END;
            """)) create.ExecuteNonQuery();
        EnsureColumn("plot_capture_events", "house_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("plot_capture_events", "house_version", "INTEGER NOT NULL DEFAULT 0");
        EnsureColumn("plot_capture_events", "blueprint_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("plot_house_history", "previous_house_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("plot_house_history", "blueprint_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("plot_houses", "blueprint_id", "TEXT NOT NULL DEFAULT ''");
        EnsureColumn("plot_houses", "house_x", "INTEGER");
        EnsureColumn("plot_houses", "house_y", "INTEGER");
        using var done = Command("SELECT COUNT(*) FROM multiplayer_schema_migrations WHERE name='plot_ownership_v1'");
        if (Convert.ToInt32(done.ExecuteScalar()) == 0)
        {
            using var migration = Command("""
                INSERT INTO plot_owners(world_id,plot_id,steam_id)
                  SELECT world_id,plot_id,steam_id FROM assignments;
                CREATE TABLE assignments_v1 (
                  world_id TEXT NOT NULL,steam_id TEXT NOT NULL,plot_id TEXT NOT NULL,spawn_id TEXT NOT NULL,
                  house_seed TEXT NOT NULL,house_x INTEGER NOT NULL,house_y INTEGER NOT NULL,created INTEGER NOT NULL,
                  PRIMARY KEY(world_id,steam_id));
                INSERT INTO assignments_v1 SELECT world_id,steam_id,plot_id,spawn_id,house_seed,house_x,house_y,created FROM assignments;
                DROP TABLE assignments;
                ALTER TABLE assignments_v1 RENAME TO assignments;
                INSERT INTO multiplayer_schema_migrations(name) VALUES('plot_ownership_v1');
                """);
            migration.ExecuteNonQuery();
        }
        // Previous releases stored only the assignment's preferred location.
        // Recover it only when the plot has exactly one assignment; the world
        // must still verify that its physical house matches before admitting.
        using (var migrateCoordinates = Command("""
            UPDATE plot_houses SET
              house_x=(SELECT a.house_x FROM assignments a WHERE a.world_id=plot_houses.world_id AND a.plot_id=plot_houses.plot_id),
              house_y=(SELECT a.house_y FROM assignments a WHERE a.world_id=plot_houses.world_id AND a.plot_id=plot_houses.plot_id)
            WHERE house_x IS NULL AND house_y IS NULL AND
              (SELECT COUNT(*) FROM assignments a WHERE a.world_id=plot_houses.world_id AND a.plot_id=plot_houses.plot_id)=1
            """)) migrateCoordinates.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteCommand Command(string sql, params (string Key, object Value)[] values)
    {
        var command = _db.CreateCommand();
        command.CommandText = sql;
        foreach (var pair in values) command.Parameters.AddWithValue(pair.Key, pair.Value);
        return command;
    }

    private void EnsureColumn(string table, string column, string declaration)
    {
        using var command = Command($"PRAGMA table_info({table})");
        using var reader = command.ExecuteReader();
        bool exists = false;
        while (reader.Read()) exists |= reader.GetString(1) == column;
        reader.Close();
        if (exists) return;
        using var migrate = Command($"ALTER TABLE {table} ADD COLUMN {column} {declaration}");
        migrate.ExecuteNonQuery();
    }

    private static long Now => DateTimeOffset.UtcNow.ToUnixTimeSeconds();

    private static readonly JsonSerializerOptions MapJson = new(JsonSerializerDefaults.Web) {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower
    };

    // The worker writes this binding in the same transaction as the fresh generation.
    // A root-owned proof must independently be checked by the world process.
    private bool FreshStartAuthorized(string worldId, long generation, string nodeId, string bootNonce, string jobId)
    {
        if (!ValidHouseId(jobId) || !ValidHouseId(bootNonce) || generation is <= 0 or > 9007199254740991L
            || string.IsNullOrEmpty(nodeId) || nodeId.Length > 64
            || nodeId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')) return false;
        using var query = Command("""
            SELECT COUNT(*) FROM world_start_authorizations a
            JOIN worlds w ON w.id=a.world_id
            WHERE a.world_id=$world AND a.job_id=$job AND a.node_id=$node
              AND a.generation=$generation AND a.boot_nonce=$nonce AND w.status='closed'
              AND ((a.mode='fresh' AND EXISTS (SELECT 1 FROM world_reset_jobs j
                    WHERE j.id=a.job_id AND j.world_id=a.world_id AND j.phase='starting'))
                OR (a.mode='bootstrap' AND EXISTS (SELECT 1 FROM world_bootstrap_jobs b
                    WHERE b.id=a.job_id AND b.world_id=a.world_id AND b.phase='starting')))
              AND w.generation=a.generation AND w.boot_nonce=a.boot_nonce
            """, ("$world", worldId), ("$job", jobId), ("$node", nodeId),
            ("$generation", generation), ("$nonce", bootNonce));
        return Convert.ToInt32(query.ExecuteScalar()) == 1;
    }

    public bool CheckFreshStart(string worldId, long generation, string nodeId, string bootNonce, string jobId)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            return FreshStartAuthorized(worldId, generation, nodeId, bootNonce, jobId);
        }
    }

    private bool RestoreStartAuthorized(string worldId, long generation, string nodeId, string bootNonce, string jobId)
    {
        if (!ValidHouseId(jobId) || !ValidHouseId(bootNonce) || generation is <= 0 or > 9007199254740991L
            || string.IsNullOrEmpty(nodeId) || nodeId.Length > 64
            || nodeId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')) return false;
        using var query = Command("""
            SELECT COUNT(*) FROM world_start_authorizations a
            JOIN worlds w ON w.id=a.world_id
            JOIN world_reset_jobs j ON j.id=a.job_id AND j.world_id=a.world_id
            JOIN official_world_maps m ON m.world_id=a.world_id AND m.generation=a.generation
            WHERE a.world_id=$world AND a.job_id=$job AND a.node_id=$node AND a.mode='restore'
              AND a.generation=$generation AND a.boot_nonce=$nonce AND w.status='closed'
              AND w.generation=a.generation AND w.boot_nonce=a.boot_nonce AND j.phase='recovering'
            """, ("$world", worldId), ("$job", jobId), ("$node", nodeId),
            ("$generation", generation), ("$nonce", bootNonce));
        return Convert.ToInt32(query.ExecuteScalar()) == 1;
    }

    public bool CheckRestoreStart(string worldId, long generation, string nodeId, string bootNonce, string jobId)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            return RestoreStartAuthorized(worldId, generation, nodeId, bootNonce, jobId);
        }
    }

    // Resume only the exact generation authorised by the completed reset. The
    // world process must additionally hold the exclusive checkpoint lease.
    public bool CheckResumeStart(string worldId, long generation, string nodeId, string bootNonce, string jobId)
    {
        if (!ValidHouseId(jobId) || !ValidHouseId(bootNonce) || generation is <= 0 or > 9007199254740991L
            || string.IsNullOrEmpty(nodeId) || nodeId.Length > 64
            || nodeId.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '_' && c != '-')) return false;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            using var query = Command("""
                SELECT COUNT(*) FROM world_start_authorizations a
                JOIN worlds w ON w.id=a.world_id
                JOIN official_world_maps m ON m.world_id=w.id AND m.generation=w.generation
                WHERE a.world_id=$world AND a.job_id=$job AND a.node_id=$node
                  AND a.generation=$generation AND a.boot_nonce=$nonce AND w.status='open'
                  AND ((a.mode='fresh' AND EXISTS (SELECT 1 FROM world_reset_jobs j
                        WHERE j.id=a.job_id AND j.world_id=a.world_id AND j.phase='complete'))
                    OR (a.mode='bootstrap' AND EXISTS (SELECT 1 FROM world_bootstrap_jobs b
                        WHERE b.id=a.job_id AND b.world_id=a.world_id AND b.phase='complete'))
                    OR (a.mode='restore' AND EXISTS (SELECT 1 FROM world_reset_jobs j
                        WHERE j.id=a.job_id AND j.world_id=a.world_id AND j.phase='recovered')))
                  AND w.generation=a.generation AND w.boot_nonce=a.boot_nonce
                """, ("$world", worldId), ("$job", jobId), ("$node", nodeId),
                ("$generation", generation), ("$nonce", bootNonce));
            return Convert.ToInt32(query.ExecuteScalar()) == 1;
        }
    }

    // The caller must also authenticate the loopback request using the node key.
    // The fresh heartbeat binds its node ID to this exact world boot identity.
    private bool MapNodeAuthorized(string worldId, long generation, string nodeId, string bootNonce)
    {
        if (string.IsNullOrWhiteSpace(nodeId) || nodeId.Length > 64 || nodeId.Any(char.IsControl)
            || !ValidHouseId(bootNonce) || generation is <= 0 or > 9007199254740991L) return false;
        using var query = Command("""
            SELECT w.status,m.last_seen FROM worlds w
            JOIN node_monitor_state m ON m.world_id=w.id AND m.node_id=$node
            WHERE w.id=$world AND w.generation=$generation AND w.boot_nonce=$nonce
              AND m.generation=$generation AND m.boot_nonce=$nonce
            """, ("$world", worldId), ("$generation", generation), ("$node", nodeId), ("$nonce", bootNonce));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return false;
        string status = reader.GetString(0);
        long lastSeen = reader.GetInt64(1);
        reader.Close();
        if (lastSeen <= 0 || lastSeen > Now || Now - lastSeen > 90) return false;
        if (status == "open")
        {
            using var active = Command("""
                SELECT COUNT(*) FROM world_start_authorizations a
                WHERE a.world_id=$world AND a.node_id=$node AND a.generation=$generation
                  AND a.boot_nonce=$nonce
                  AND ((a.mode='fresh' AND EXISTS (SELECT 1 FROM world_reset_jobs j
                        WHERE j.id=a.job_id AND j.world_id=a.world_id AND j.phase='complete'))
                    OR (a.mode='bootstrap' AND EXISTS (SELECT 1 FROM world_bootstrap_jobs b
                        WHERE b.id=a.job_id AND b.world_id=a.world_id AND b.phase='complete'))
                    OR (a.mode='restore' AND EXISTS (SELECT 1 FROM world_reset_jobs j
                        WHERE j.id=a.job_id AND j.world_id=a.world_id AND j.phase='recovered')))
                """, ("$world", worldId), ("$node", nodeId), ("$generation", generation), ("$nonce", bootNonce));
            return Convert.ToInt32(active.ExecuteScalar()) == 1;
        }
        if (status != "closed") return false;
        using var authorization = Command("SELECT job_id FROM world_start_authorizations WHERE world_id=$world", ("$world", worldId));
        return authorization.ExecuteScalar() is string jobId
            && (FreshStartAuthorized(worldId, generation, nodeId, bootNonce, jobId)
                || RestoreStartAuthorized(worldId, generation, nodeId, bootNonce, jobId));
    }

    public (int Status, string Error) RegisterOfficialWorldMap(string nodeId, string bootNonce, string jobId, OfficialWorldMap? map)
    {
        if (map == null || !map.Valid()) return (400, "invalid_official_map");
        var canonical = map.Canonical();
        string serialized = JsonSerializer.Serialize(canonical, MapJson);
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            if (!MapNodeAuthorized(canonical.WorldId, canonical.Generation, nodeId, bootNonce))
                return (409, "node_identity_changed");
            using (var state = Command("SELECT status FROM worlds WHERE id=$world", ("$world", canonical.WorldId)))
                if (state.ExecuteScalar() as string != "open"
                    && !FreshStartAuthorized(canonical.WorldId, canonical.Generation, nodeId, bootNonce, jobId))
                    return (409, "start_identity_changed");
            using (var existing = Command("SELECT generation,map_json FROM official_world_maps WHERE world_id=$world", ("$world", canonical.WorldId)))
            using (var reader = existing.ExecuteReader())
            {
                if (reader.Read())
                {
                    bool same = reader.GetInt64(0) == canonical.Generation && reader.GetString(1) == serialized;
                    reader.Close();
                    return same && ValidMapAssignments(canonical) ? (200, "") : (409, "official_map_conflict");
                }
            }
            if (!FreshStartAuthorized(canonical.WorldId, canonical.Generation, nodeId, bootNonce, jobId))
                return (409, "start_identity_changed");
            using (var assignments = Command("SELECT COUNT(*) FROM assignments WHERE world_id=$world", ("$world", canonical.WorldId)))
                if (Convert.ToInt32(assignments.ExecuteScalar()) != 0) return (409, "assignments_exist");
            using (var oldOwnership = Command("""
                SELECT (SELECT COUNT(*) FROM plot_owners WHERE world_id=$world)
                     + (SELECT COUNT(*) FROM plot_houses WHERE world_id=$world)
                     + (SELECT COUNT(*) FROM plot_house_history WHERE world_id=$world)
                     + (SELECT COUNT(*) FROM plot_capture_events WHERE world_id=$world)
                """, ("$world", canonical.WorldId)))
                if (Convert.ToInt32(oldOwnership.ExecuteScalar()) != 0) return (409, "assignments_exist");
            using var insert = Command("INSERT INTO official_world_maps(world_id,generation,map_json) VALUES($world,$generation,$map)",
                ("$world", canonical.WorldId), ("$generation", canonical.Generation), ("$map", serialized));
            insert.ExecuteNonQuery();
            tx.Commit();
            return (200, "");
        }
    }

    public bool HasOfficialWorldMap(string worldId)
    {
        lock (_gate)
        {
            using var query = Command("SELECT COUNT(*) FROM official_world_maps WHERE world_id=$world", ("$world", worldId));
            return Convert.ToInt32(query.ExecuteScalar()) != 0;
        }
    }

    // Called inside the same database transaction as the operation consuming the map.
    private (string Error, OfficialWorldMap? Map) CurrentOfficialMap(string worldId, long generation)
    {
        using var query = Command("""
            SELECT w.boot_nonce,m.generation,m.map_json FROM worlds w
            LEFT JOIN official_world_maps m ON m.world_id=w.id WHERE w.id=$world
            """, ("$world", worldId));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return ("world_not_found", null);
        string nonce = reader.GetString(0);
        if (reader.IsDBNull(1))
        {
            reader.Close();
            if (nonce.Length != 0) return ("official_map_missing", null);
            using var mixed = Command("SELECT COUNT(*) FROM assignments WHERE world_id=$world AND map_generation<>0", ("$world", worldId));
            return Convert.ToInt32(mixed.ExecuteScalar()) == 0 ? ("", null) : ("map_protocol_changed", null);
        }
        long mapGeneration = reader.GetInt64(1);
        string serialized = reader.GetString(2);
        reader.Close();
        if (!ValidHouseId(nonce) || mapGeneration != generation) return ("official_map_invalid", null);
        using (var node = Command("""
            SELECT COUNT(*) FROM node_monitor_state WHERE world_id=$world AND generation=$generation
              AND boot_nonce=$nonce AND last_seen BETWEEN $oldest AND $now
            """, ("$world", worldId), ("$generation", generation), ("$nonce", nonce),
            ("$oldest", Now - 90), ("$now", Now)))
            if (Convert.ToInt32(node.ExecuteScalar()) != 1) return ("official_node_unavailable", null);
        try
        {
            var map = JsonSerializer.Deserialize<OfficialWorldMap>(serialized, MapJson);
            if (map == null || !map.Valid() || map.WorldId != worldId || map.Generation != generation
                || !ValidMapAssignments(map)) return ("official_map_invalid", null);
            return ("", map);
        }
        catch (JsonException) { return ("official_map_invalid", null); }
    }

    private bool ValidMapAssignments(OfficialWorldMap map)
    {
        using var query = Command("""
            SELECT a.plot_id,a.spawn_id,a.house_x,a.house_y,a.map_generation,h.house_id,h.house_x,h.house_y
            FROM assignments a LEFT JOIN plot_houses h ON h.world_id=a.world_id AND h.plot_id=a.plot_id
            WHERE a.world_id=$world
            """, ("$world", map.WorldId));
        using var reader = query.ExecuteReader();
        while (reader.Read())
        {
            var plot = map.Plots.FirstOrDefault(p => p.Id == reader.GetString(0));
            if (plot == null || !map.Spawns.Any(s => s.Id == reader.GetString(1))
                || reader.GetInt32(2) < plot.X || reader.GetInt32(2) >= plot.X + plot.W
                || reader.GetInt32(3) < plot.Y || reader.GetInt32(3) >= plot.Y + plot.H
                || reader.GetInt64(4) != map.Generation
                || (!reader.IsDBNull(5) && (reader.IsDBNull(6) || reader.IsDBNull(7)
                    || reader.GetInt32(2) != reader.GetInt32(6) || reader.GetInt32(3) != reader.GetInt32(7)))) return false;
        }
        reader.Close();
        using (var missing = Command("""
            SELECT COUNT(*) FROM assignments a WHERE a.world_id=$world AND NOT EXISTS
              (SELECT 1 FROM plot_owners o WHERE o.world_id=a.world_id AND o.plot_id=a.plot_id)
            """, ("$world", map.WorldId)))
            if (Convert.ToInt32(missing.ExecuteScalar()) != 0) return false;
        using var owners = Command("SELECT plot_id,steam_id FROM plot_owners WHERE world_id=$world", ("$world", map.WorldId));
        using var ownerReader = owners.ExecuteReader();
        while (ownerReader.Read())
            if (!map.Plots.Any(p => p.Id == ownerReader.GetString(0)) || !ValidSteamId(ownerReader.GetString(1))) return false;
        return true;
    }

    public (int Status, string Error, OfficialWorldMap? Map) ReadOfficialWorldMap(
        string worldId, long generation, string nodeId, string bootNonce)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            if (!MapNodeAuthorized(worldId, generation, nodeId, bootNonce)) return (409, "node_identity_changed", null);
            using var query = Command("SELECT map_json FROM official_world_maps WHERE world_id=$world AND generation=$generation",
                ("$world", worldId), ("$generation", generation));
            if (query.ExecuteScalar() is not string serialized) return (404, "official_map_missing", null);
            try
            {
                var map = JsonSerializer.Deserialize<OfficialWorldMap>(serialized, MapJson);
                return map != null && map.Valid() && map.WorldId == worldId && map.Generation == generation
                    ? (200, "", map) : (503, "official_map_invalid", null);
            }
            catch (JsonException) { return (503, "official_map_invalid", null); }
        }
    }

    public string CheckWorldLease(string worldId, long generation, string nodeId, string bootNonce)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            if (CurrentOfficialMap(worldId, generation).Error.Length != 0) return "";
            string status = FindWorld(worldId)?.Status ?? "";
            if (status == "open" && MapNodeAuthorized(worldId, generation, nodeId, bootNonce)) return "active";
            if (status != "closed" || !ValidHouseId(bootNonce)) return "";
            using var query = Command("""
                SELECT COUNT(*) FROM worlds w
                JOIN world_start_authorizations a ON a.world_id=w.id
                JOIN node_monitor_state n ON n.world_id=w.id AND n.node_id=a.node_id
                WHERE w.id=$world AND w.generation=$generation AND w.boot_nonce=$nonce
                  AND a.node_id=$node AND a.generation=$generation AND a.boot_nonce=$nonce
                  AND ((a.mode='fresh' AND EXISTS (SELECT 1 FROM world_reset_jobs old
                        WHERE old.id=a.job_id AND old.world_id=w.id AND old.phase='complete'))
                    OR (a.mode='bootstrap' AND EXISTS (SELECT 1 FROM world_bootstrap_jobs old
                        WHERE old.id=a.job_id AND old.world_id=w.id AND old.phase='complete'))
                    OR (a.mode='restore' AND EXISTS (SELECT 1 FROM world_reset_jobs old
                        WHERE old.id=a.job_id AND old.world_id=w.id AND old.phase='recovered')))
                  AND n.generation=$generation AND n.boot_nonce=$nonce AND n.last_seen BETWEEN $oldest AND $now
                  AND EXISTS (SELECT 1 FROM world_reset_jobs j WHERE j.world_id=w.id AND j.phase IN ('queued','stopping'))
                """, ("$world", worldId), ("$generation", generation), ("$node", nodeId), ("$nonce", bootNonce),
                ("$oldest", Now - 90), ("$now", Now));
            return Convert.ToInt32(query.ExecuteScalar()) == 1 ? "draining" : "";
        }
    }

    private bool EntryReadyInTransaction(string worldId, long generation)
    {
        if (FindWorld(worldId)?.Status != "open") return false;
        using var query = Command("""
            SELECT node_id,boot_nonce,last_seen FROM world_entry_readiness
            WHERE world_id=$world AND generation=$generation
            """, ("$world", worldId), ("$generation", generation));
        using var reader = query.ExecuteReader();
        if (!reader.Read()) return false;
        string node = reader.GetString(0), nonce = reader.GetString(1);
        long at = reader.GetInt64(2);
        reader.Close();
        return at <= Now && Now - at <= 3 && MapNodeAuthorized(worldId, generation, node, nonce)
            && CurrentOfficialMap(worldId, generation).Error.Length == 0;
    }

    public bool WorldEntryReady(string worldId, long generation)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            return EntryReadyInTransaction(worldId, generation);
        }
    }

    public bool RenewWorldEntryReady(string worldId, long generation, string nodeId, string bootNonce, long revision)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            if (FindWorld(worldId)?.Status != "open" || !MapNodeAuthorized(worldId, generation, nodeId, bootNonce)
                || CurrentOfficialMap(worldId, generation).Error.Length != 0 || revision <= 0) return false;
            using (var current = Command("SELECT COALESCE((SELECT revision FROM plot_snapshot_revisions WHERE world_id=$world),1)",
                       ("$world", worldId)))
                if (Convert.ToInt64(current.ExecuteScalar()) != revision) return false;
            using (var missing = Command("""
                SELECT COUNT(*) FROM plot_owners o LEFT JOIN plot_houses h
                  ON h.world_id=o.world_id AND h.plot_id=o.plot_id
                WHERE o.world_id=$world AND h.house_id IS NULL
                """, ("$world", worldId)))
                if (Convert.ToInt64(missing.ExecuteScalar()) != 0) return false;
            using var upsert = Command("""
                INSERT INTO world_entry_readiness(world_id,generation,node_id,boot_nonce,last_seen)
                VALUES($world,$generation,$node,$nonce,$at)
                ON CONFLICT(world_id) DO UPDATE SET generation=excluded.generation,node_id=excluded.node_id,
                  boot_nonce=excluded.boot_nonce,last_seen=excluded.last_seen
                """, ("$world", worldId), ("$generation", generation), ("$node", nodeId), ("$nonce", bootNonce), ("$at", Now));
            upsert.ExecuteNonQuery(); tx.Commit(); return true;
        }
    }

    public bool RevokeWorldEntryReady(string worldId, long generation, string nodeId, string bootNonce)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            if (FindWorld(worldId)?.Status != "open" || !MapNodeAuthorized(worldId, generation, nodeId, bootNonce)) return false;
            using var remove = Command("""
                DELETE FROM world_entry_readiness WHERE world_id=$world AND generation=$generation
                  AND node_id=$node AND boot_nonce=$nonce
                """, ("$world", worldId), ("$generation", generation), ("$node", nodeId), ("$nonce", bootNonce));
            remove.ExecuteNonQuery(); tx.Commit(); return true;
        }
    }

    // A consistent database view, restricted to the currently authorized node.
    public (int Status, string Error, long Revision, IReadOnlyList<PlotHouseSnapshot>? Plots) ReadPlotHouseSnapshot(
        string worldId, long generation, string nodeId, string bootNonce)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            if (!MapNodeAuthorized(worldId, generation, nodeId, bootNonce))
                return (409, "node_identity_changed", 0, null);
            var (error, map) = CurrentOfficialMap(worldId, generation);
            if (error.Length != 0 || map == null)
                return (503, "official_map_invalid", 0, null);
            using (var orphan = Command("""
                SELECT COUNT(*) FROM plot_houses h WHERE h.world_id=$world AND NOT EXISTS
                  (SELECT 1 FROM plot_owners o WHERE o.world_id=h.world_id AND o.plot_id=h.plot_id)
                """, ("$world", worldId)))
                if (Convert.ToInt64(orphan.ExecuteScalar()) != 0) return (503, "house_snapshot_invalid", 0, null);
            using var revisionQuery = Command("SELECT revision FROM plot_snapshot_revisions WHERE world_id=$world", ("$world", worldId));
            long revision = Convert.ToInt64(revisionQuery.ExecuteScalar() ?? 1L);
            if (revision is <= 0 or > 9007199254740991L) return (503, "house_snapshot_invalid", 0, null);
            using var query = Command("""
                SELECT o.plot_id,o.steam_id,h.house_id,h.house_version,h.state,
                  (SELECT COUNT(*) FROM plot_house_history v WHERE v.world_id=h.world_id
                    AND v.house_id=h.house_id AND v.plot_id=h.plot_id AND v.house_version=h.house_version
                    AND v.blueprint_id=h.blueprint_id),
                  (SELECT COUNT(*) FROM assignments a WHERE a.world_id=o.world_id AND a.steam_id=o.steam_id),
                  h.blueprint_id,h.house_x,h.house_y
                FROM plot_owners o LEFT JOIN plot_houses h ON h.world_id=o.world_id AND h.plot_id=o.plot_id
                WHERE o.world_id=$world ORDER BY o.plot_id
                """, ("$world", worldId));
            using var reader = query.ExecuteReader();
            var plots = new List<PlotHouseSnapshot>();
            var houses = new HashSet<string>(StringComparer.Ordinal);
            while (reader.Read())
            {
                string plot = reader.GetString(0), owner = reader.GetString(1);
                var plotBounds = map.Plots.FirstOrDefault(p => p.Id == plot);
                if (plotBounds == null || !ValidSteamId(owner) || reader.GetInt64(6) != 1)
                    return (503, "house_snapshot_invalid", 0, null);
                if (reader.IsDBNull(2))
                {
                    plots.Add(new PlotHouseSnapshot(plot, owner, null, null, null, null, null, null));
                    continue;
                }
                string house = reader.GetString(2), state = reader.GetString(4);
                string blueprint = reader.GetString(7);
                long version = reader.GetInt64(3);
                if (reader.IsDBNull(8) || reader.IsDBNull(9)) return (503, "house_snapshot_invalid", 0, null);
                int x = reader.GetInt32(8), y = reader.GetInt32(9);
                if (x < plotBounds.X || x >= plotBounds.X + plotBounds.W || y < plotBounds.Y || y >= plotBounds.Y + plotBounds.H
                    || !ValidHouseId(house) || !houses.Add(house) || version is <= 0 or > 9007199254740991L
                    || state is not ("intact" or "destroyed" or "claimed") || reader.GetInt64(5) != 1
                    || (state == "intact" ? blueprint != "" : !ValidHouseId(blueprint) || !houses.Add(blueprint)))
                    return (503, "house_snapshot_invalid", 0, null);
                plots.Add(new PlotHouseSnapshot(plot, owner, house, version, state, blueprint, x, y));
            }
            return (200, "", revision, plots);
        }
    }

    public static bool ValidSteamId(string? id)
        => id is { Length: 17 } && id.All(char.IsAsciiDigit);

    public IReadOnlyList<MultiplayerWorld> ListWorlds()
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT w.id,w.name,w.map_id,w.status,w.capacity,w.ranked,
                       (SELECT COUNT(*) FROM assignments a WHERE a.world_id=w.id)
                FROM worlds w ORDER BY w.id
                """);
            using var reader = command.ExecuteReader();
            var list = new List<MultiplayerWorld>();
            while (reader.Read())
                list.Add(new MultiplayerWorld(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetInt32(4), reader.GetInt32(5) != 0, reader.GetInt32(6)));
            return list;
        }
    }

    public MultiplayerWorld? FindWorld(string id)
        => ListWorlds().FirstOrDefault(world => string.Equals(world.Id, id, StringComparison.Ordinal));

    public long? OpenGeneration(string worldId)
    {
        lock (_gate)
        {
            using var command = Command("SELECT generation FROM worlds WHERE id=$id AND status='open'", ("$id", worldId));
            var value = command.ExecuteScalar();
            return value is long generation && generation is > 0 and <= 9007199254740991L ? generation : null;
        }
    }

    public WorldResetStatusRecord WorldResetStatus(string worldId)
    {
        lock (_gate)
        {
            using var heartbeat = Command("SELECT last_seen FROM world_reset_worker WHERE world_id=$world AND protocol_version=1", ("$world", worldId));
            long lastSeen = heartbeat.ExecuteScalar() is long value ? value : 0;
            using var query = Command("SELECT id,phase,error,created,updated FROM world_reset_jobs WHERE world_id=$world ORDER BY created DESC,rowid DESC LIMIT 1", ("$world", worldId));
            using var reader = query.ExecuteReader();
            return reader.Read()
                ? new WorldResetStatusRecord(lastSeen, reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetInt64(3), reader.GetInt64(4))
                : new WorldResetStatusRecord(lastSeen, "", "", "", 0, 0);
        }
    }

    public bool CanQueueWorldReset(string worldId)
    {
        if (worldId != "official-1" || Environment.GetEnvironmentVariable("WHEATSTEAD_RESET_WORKER_ENABLED") != "1") return false;
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction(deferred: false);
            var status = WorldResetStatus(worldId);
            long? generation = OpenGeneration(worldId);
            return status.WorkerLastSeen > 0 && status.WorkerLastSeen <= Now && Now - status.WorkerLastSeen <= 90
                && status.Phase is "" or "complete" or "recovered" && generation is > 0
                && EntryReadyInTransaction(worldId, generation.Value);
        }
    }

    public (bool Ok, string Error, string JobId) QueueWorldReset(string worldId)
    {
        if (worldId != "official-1" || Environment.GetEnvironmentVariable("WHEATSTEAD_RESET_WORKER_ENABLED") != "1")
            return (false, "reset_unavailable", "");
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction(deferred: false);
            var status = WorldResetStatus(worldId);
            if (status.WorkerLastSeen <= 0 || status.WorkerLastSeen > Now || Now - status.WorkerLastSeen > 90)
                return (false, "reset_worker_offline", "");
            if (status.Phase is not ("" or "complete" or "recovered")) return (false, "reset_in_progress", "");
            // Every house transition revokes readiness before staging its durable
            // outbox event. The same write transaction serializes that revoke
            // with closing the world, so a pending/in-flight transition cannot
            // be stranded by a newly queued reset.
            var current = FindWorld(worldId);
            long? generation = OpenGeneration(worldId);
            if (current?.Status != "open" || generation is not > 0 || !EntryReadyInTransaction(worldId, generation.Value))
                return (false, "world_unavailable", "");
            using var update = Command("UPDATE worlds SET status='closed' WHERE id=$world AND status='open' AND generation < 9007199254740991", ("$world", worldId));
            if (update.ExecuteNonQuery() != 1) return (false, "world_unavailable", "");
            string id = Guid.NewGuid().ToString("N");
            using var insert = Command("INSERT INTO world_reset_jobs(id,world_id,phase,created,updated) VALUES($id,$world,'queued',$at,$at)", ("$id", id), ("$world", worldId), ("$at", Now));
            insert.ExecuteNonQuery();
            Audit("world_reset_queued", $"{worldId}/{id}");
            transaction.Commit();
            return (true, "", id);
        }
    }

    /// <summary>地图 + 已占用的地块 ID；世界或地图不存在时返回 null。</summary>
    public (MultiplayerWorld World, MultiplayerMapDef Map, IReadOnlyList<string> Occupied)? Map(string worldId)
    {
        var world = FindWorld(worldId);
        if (world == null) return null;
        var map = MultiplayerMaps.Get(world.MapId);
        if (map == null) return null;
        lock (_gate)
        {
            using var command = Command("SELECT plot_id FROM plot_owners WHERE world_id=$world ORDER BY plot_id",
                ("$world", worldId));
            using var reader = command.ExecuteReader();
            var occupied = new List<string>();
            while (reader.Read()) occupied.Add(reader.GetString(0));
            return (world, map, occupied);
        }
    }

    public (int Status, string Error, MultiplayerWorld? World, object? Map, IReadOnlyList<string>? Occupied, long Generation, string Source)
        PublicMap(string worldId)
    {
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction(deferred: false);
            var world = FindWorld(worldId);
            if (world == null) return (404, "world_not_found", null, null, null, 0, "");
            using var generationQuery = Command("SELECT generation FROM worlds WHERE id=$world", ("$world", worldId));
            long generation = generationQuery.ExecuteScalar() is long value ? value : 0;
            var (error, actual) = CurrentOfficialMap(worldId, generation);
            if (error.Length > 0) return (503, error, null, null, null, 0, "");
            if (actual != null && world.Status != "open") return (409, "world_closed", null, null, null, 0, "");
            var legacy = actual == null ? MultiplayerMaps.Get(world.MapId) : null;
            if (actual == null && legacy == null) return (503, "map_missing", null, null, null, 0, "");
            using var occupiedQuery = Command("SELECT plot_id FROM plot_owners WHERE world_id=$world ORDER BY plot_id", ("$world", worldId));
            using var reader = occupiedQuery.ExecuteReader();
            var occupied = new List<string>();
            while (reader.Read()) occupied.Add(reader.GetString(0));
            return (200, "", world, (object?)actual ?? legacy, occupied, generation, actual == null ? "legacy" : "official");
        }
    }

    public (int Status, string Error, MultiplayerAssignment? Assignment) Assign(
        string worldId, string steamId, string plotId, string spawnId, long generation, long? selectedGeneration = null,
        bool requireReady = false)
    {
        if (!ValidSteamId(steamId)) return (401, "invalid_steam_identity", null);
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction(deferred: false);
            var world = FindWorld(worldId);
            if (world == null) return (404, "world_not_found", null);
            if (world.Status != "open") return (409, "world_closed", null);
            if (OpenGeneration(worldId) != generation) return (401, "world_generation_changed", null);
            if (requireReady && !EntryReadyInTransaction(worldId, generation)) return (503, "world_unavailable", null);
            var (mapError, actual) = CurrentOfficialMap(worldId, generation);
            if (mapError.Length > 0) return (503, mapError, null);
            if (actual != null && selectedGeneration != generation) return (409, "world_generation_changed", null);
            if (actual == null && selectedGeneration.HasValue) return (409, "map_protocol_changed", null);
            var map = actual == null ? MultiplayerMaps.Get(world.MapId) : null;
            if (actual == null && map == null) return (500, "map_missing", null);
            var realPlot = actual?.Plots.FirstOrDefault(p => p.Id == plotId);
            var oldPlot = map?.Plots.FirstOrDefault(p => p.Id == plotId);
            if (realPlot == null && oldPlot == null) return (400, "invalid_plot", null);
            if (!(actual?.Spawns.Any(s => s.Id == spawnId) ?? map!.Spawns.Any(s => s.Id == spawnId)))
                return (400, "invalid_spawn", null);
            int houseX = realPlot != null ? realPlot.X + realPlot.W / 2 : oldPlot!.X + oldPlot.W / 2;
            int houseY = realPlot != null ? realPlot.Y + realPlot.H / 2 : oldPlot!.Y + oldPlot.H / 2;

            var existing = FindAssignment(worldId, steamId);
            if (existing != null) return (200, "", existing);
            using (var count = Command("SELECT COUNT(*) FROM assignments WHERE world_id=$world", ("$world", worldId)))
                if (Convert.ToInt32(count.ExecuteScalar()) >= world.Capacity)
                    return (409, "world_full", null);
            using (var taken = Command("SELECT COUNT(*) FROM plot_owners WHERE world_id=$world AND plot_id=$plot",
                       ("$world", worldId), ("$plot", plotId)))
                if (Convert.ToInt32(taken.ExecuteScalar()) > 0)
                    return (409, "plot_taken", null);

            var assignment = new MultiplayerAssignment(worldId, steamId, plotId, spawnId,
                Convert.ToHexString(RandomNumberGenerator.GetBytes(8)).ToLowerInvariant(),
                houseX, houseY, Now, actual == null ? 0 : generation);
            try
            {
                using var insert = Command("""
                    INSERT INTO assignments(world_id,steam_id,plot_id,spawn_id,house_seed,house_x,house_y,created,map_generation)
                    VALUES($world,$steam,$plot,$spawn,$seed,$x,$y,$at,$generation)
                    """, ("$world", assignment.WorldId), ("$steam", assignment.SteamId), ("$plot", assignment.PlotId),
                    ("$spawn", assignment.SpawnId), ("$seed", assignment.HouseSeed), ("$x", assignment.HouseX),
                    ("$y", assignment.HouseY), ("$at", assignment.Created), ("$generation", assignment.MapGeneration));
                insert.ExecuteNonQuery();
                using var own = Command("INSERT INTO plot_owners(world_id,plot_id,steam_id) VALUES($world,$plot,$steam)",
                    ("$world", worldId), ("$plot", plotId), ("$steam", steamId));
                own.ExecuteNonQuery();
                // A new owner has no physical house yet. Atomically revoke the
                // previous readiness so reset/admission cannot race the world's
                // asynchronous initial-house creation and checkpoint commit.
                using var revoke = Command("DELETE FROM world_entry_readiness WHERE world_id=$world", ("$world", worldId));
                revoke.ExecuteNonQuery();
            }
            catch (SqliteException ex) when (ex.SqliteErrorCode == 19)
            {
                return (409, "plot_taken", null);
            }
            Audit("assign", $"{worldId}/{steamId}/{plotId}");
            transaction.Commit();
            return (200, "", assignment);
        }
    }

    private MultiplayerAssignment? FindAssignment(string worldId, string steamId)
    {
        using var command = Command("""
            SELECT world_id,steam_id,plot_id,spawn_id,house_seed,house_x,house_y,created,map_generation
            FROM assignments WHERE world_id=$world AND steam_id=$steam
            """, ("$world", worldId), ("$steam", steamId));
        using var reader = command.ExecuteReader();
        return reader.Read()
            ? new MultiplayerAssignment(reader.GetString(0), reader.GetString(1), reader.GetString(2), reader.GetString(3),
                reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt64(7), reader.GetInt64(8))
            : null;
    }

    /// <summary>在同一个锁内核对世界状态与玩家已有分配，供可信世界进程入场时查询。</summary>
    public (int Status, string Error, MultiplayerAssignment? Assignment, MultiplayerSpawnDef? Spawn) CheckAdmission(
        string worldId, string steamId, long generation, long? requestedGeneration = null,
        bool requireNode = false, string? nodeId = null, string? bootNonce = null)
    {
        if (!ValidSteamId(steamId)) return (400, "invalid_steam_identity", null, null);
        lock (_gate)
        {
            using var transaction = _db.BeginTransaction(deferred: false);
            var world = FindWorld(worldId);
            if (world == null) return (404, "world_not_found", null, null);
            if (world.Status != "open") return (409, "world_closed", null, null);
            if (OpenGeneration(worldId) != generation) return (401, "world_generation_changed", null, null);
            if (requireNode && !EntryReadyInTransaction(worldId, generation)) return (503, "world_unavailable", null, null);
            var (mapError, actual) = CurrentOfficialMap(worldId, generation);
            if (mapError.Length > 0) return (503, mapError, null, null);
            if (actual != null && !requireNode && requestedGeneration != generation)
                return (409, "world_generation_changed", null, null);
            if (actual == null && requestedGeneration.HasValue) return (409, "map_protocol_changed", null, null);
            if (actual != null && requireNode && !MapNodeAuthorized(worldId, generation, nodeId ?? "", bootNonce ?? ""))
                return (409, "node_identity_changed", null, null);
            var assignment = FindAssignment(worldId, steamId);
            if (assignment == null) return (403, "assignment_required", null, null);
            if (actual != null)
            {
                var realSpawn = actual.Spawns.FirstOrDefault(s => s.Id == assignment.SpawnId);
                return realSpawn == null ? (503, "assignment_invalid", null, null)
                    : (200, "", assignment, new MultiplayerSpawnDef(realSpawn.Id, "", realSpawn.X, realSpawn.Y));
            }
            if (assignment.MapGeneration != 0) return (503, "assignment_invalid", null, null);
            var map = MultiplayerMaps.Get(world.MapId);
            if (map == null) return (503, "map_missing", null, null);
            var plot = map.Plots.FirstOrDefault(p => p.Id == assignment.PlotId);
            var spawn = map.Spawns.FirstOrDefault(s => s.Id == assignment.SpawnId);
            if (plot == null || spawn == null) return (503, "assignment_invalid", null, null);
            return (200, "", assignment, spawn);
        }
    }

    public IReadOnlyList<MultiplayerAssignment> Assignments(string worldId)
    {
        lock (_gate)
        {
            using var command = Command("""
                SELECT world_id,steam_id,plot_id,spawn_id,house_seed,house_x,house_y,created,map_generation
                FROM assignments WHERE world_id=$world ORDER BY created
                """, ("$world", worldId));
            using var reader = command.ExecuteReader();
            var list = new List<MultiplayerAssignment>();
            while (reader.Read())
                list.Add(new MultiplayerAssignment(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt32(6), reader.GetInt64(7), reader.GetInt64(8)));
            return list;
        }
    }

    public IReadOnlyDictionary<string, string> PlotOwners(string worldId)
    {
        lock (_gate)
        {
            using var command = Command("SELECT plot_id,steam_id FROM plot_owners WHERE world_id=$world ORDER BY plot_id",
                ("$world", worldId));
            using var reader = command.ExecuteReader();
            var owners = new Dictionary<string, string>(StringComparer.Ordinal);
            while (reader.Read()) owners.Add(reader.GetString(0), reader.GetString(1));
            return owners;
        }
    }

    public bool SetStatus(string worldId, string status)
    {
        if (status is not ("open" or "closed")) return false;
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            if (status == "open")
            {
                var reset = WorldResetStatus(worldId);
                if (reset.Phase is not ("" or "complete" or "recovered")) return false;
                using var generationQuery = Command("SELECT generation FROM worlds WHERE id=$world", ("$world", worldId));
                if (generationQuery.ExecuteScalar() is not long generation) return false;
                if (CurrentOfficialMap(worldId, generation).Error.Length > 0) return false;
                // Reopening an inhabited registered world needs world-save/outbox reconciliation.
                using var inhabited = Command("""
                    SELECT COUNT(*) FROM official_world_maps m WHERE m.world_id=$world AND EXISTS
                      (SELECT 1 FROM assignments a WHERE a.world_id=m.world_id)
                    """, ("$world", worldId));
                if (Convert.ToInt32(inhabited.ExecuteScalar()) != 0) return false;
            }
            using var command = Command("UPDATE worlds SET status=$status WHERE id=$id", ("$status", status), ("$id", worldId));
            bool changed = command.ExecuteNonQuery() > 0;
            if (changed) Audit("world_" + status, worldId);
            tx.Commit();
            return changed;
        }
    }

    public bool Release(string worldId, string steamId)
    {
        lock (_gate)
        {
            var reset = WorldResetStatus(worldId);
            if (reset.Phase is not ("" or "complete" or "recovered")) return false;
            using var transaction = _db.BeginTransaction(deferred: false);
            // A registered world may have a house event durably staged before
            // its control-side house row exists. Directly releasing an owner here
            // can strand that event or orphan physical buildings and items.
            using (var guarded = Command("""
                SELECT COUNT(*) FROM official_world_maps WHERE world_id=$world
                """, ("$world", worldId)))
                if (Convert.ToInt64(guarded.ExecuteScalar()) != 0) return false;
            using var command = Command("DELETE FROM assignments WHERE world_id=$world AND steam_id=$steam",
                ("$world", worldId), ("$steam", steamId));
            bool changed = command.ExecuteNonQuery() > 0;
            if (changed)
            {
                using var houses = Command("""
                    DELETE FROM plot_houses WHERE world_id=$world AND plot_id IN
                      (SELECT plot_id FROM plot_owners WHERE world_id=$world AND steam_id=$steam)
                    """, ("$world", worldId), ("$steam", steamId));
                houses.ExecuteNonQuery();
                using var owners = Command("DELETE FROM plot_owners WHERE world_id=$world AND steam_id=$steam",
                    ("$world", worldId), ("$steam", steamId));
                owners.ExecuteNonQuery();
                Audit("release", $"{worldId}/{steamId}");
            }
            transaction.Commit();
            return changed;
        }
    }

    private static bool ValidHouseId(string? id)
        => id is { Length: 32 } && id.All(char.IsAsciiHexDigitLower);

    // Internal methods only: a trusted world must durably persist and verify
    // these transitions before any node API is permitted to call them.
    public (bool Ok, string Error) RegisterPlotHouse(string worldId, string plotId, string owner,
        long generation, string houseId, string previousHouseId, long previousVersion, string? nodeId = null, string? bootNonce = null,
        int? cellX = null, int? cellY = null)
    {
        if (!ValidSteamId(owner) || !ValidHouseId(houseId) || previousVersion < 0
            || previousVersion >= 9007199254740991L
            || !cellX.HasValue || !cellY.HasValue
            || (previousVersion == 0 ? previousHouseId != "" : !ValidHouseId(previousHouseId)))
            return (false, "invalid_house");
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            if ((nodeId != null || bootNonce != null) && !MapNodeAuthorized(worldId, generation, nodeId ?? "", bootNonce ?? ""))
                return (false, "node_identity_changed");
            if (OpenGeneration(worldId) != generation) return (false, "world_generation_changed");
            var (mapError, map) = CurrentOfficialMap(worldId, generation);
            var plot = map?.Plots.FirstOrDefault(p => p.Id == plotId);
            if (mapError.Length != 0 || plot == null || cellX.Value < plot.X || cellX.Value >= plot.X + plot.W
                || cellY.Value < plot.Y || cellY.Value >= plot.Y + plot.H) return (false, "invalid_house_position");
            using (var check = Command("SELECT COUNT(*) FROM plot_owners WHERE world_id=$world AND plot_id=$plot AND steam_id=$owner",
                ("$world", worldId), ("$plot", plotId), ("$owner", owner)))
                if (Convert.ToInt32(check.ExecuteScalar()) != 1) return (false, "plot_owner_changed");
            using (var existing = Command("SELECT house_id,house_version,state,house_x,house_y FROM plot_houses WHERE world_id=$world AND plot_id=$plot",
                ("$world", worldId), ("$plot", plotId)))
            using (var reader = existing.ExecuteReader())
            {
                if (reader.Read())
                {
                    string currentId = reader.GetString(0);
                    long currentVersion = reader.GetInt64(1);
                    string state = reader.GetString(2);
                    if (reader.IsDBNull(3) || reader.IsDBNull(4) || reader.GetInt32(3) != cellX.Value || reader.GetInt32(4) != cellY.Value)
                        return (false, "house_position_changed");
                    if (currentId == houseId && currentVersion == previousVersion + 1 && state == "intact")
                    {
                        reader.Close();
                        using var replay = Command("""
                            SELECT COUNT(*) FROM plot_house_history WHERE world_id=$world AND house_id=$house
                              AND plot_id=$plot AND house_version=$version AND previous_house_id=$previous
                            """, ("$world", worldId), ("$house", houseId), ("$plot", plotId),
                            ("$version", previousVersion + 1), ("$previous", previousHouseId));
                        return Convert.ToInt32(replay.ExecuteScalar()) == 1 ? (true, "") : (false, "house_changed");
                    }
                    if (previousVersion == 0 || currentId != previousHouseId || currentVersion != previousVersion
                        || state is not ("destroyed" or "claimed")) return (false, "house_changed");
                }
                else if (previousVersion != 0) return (false, "house_changed");
            }
            using (var history = Command("SELECT COUNT(*) FROM plot_house_history WHERE world_id=$world AND (house_id=$house OR blueprint_id=$house)",
                ("$world", worldId), ("$house", houseId)))
                if (Convert.ToInt32(history.ExecuteScalar()) != 0) return (false, "house_id_reused");
            using (var insert = Command("""
                INSERT INTO plot_houses(world_id,plot_id,house_id,house_version,state,house_x,house_y)
                VALUES($world,$plot,$house,$version,'intact',$x,$y)
                ON CONFLICT(world_id,plot_id) DO UPDATE SET house_id=excluded.house_id,
                  house_version=excluded.house_version,state='intact',blueprint_id='',house_x=excluded.house_x,house_y=excluded.house_y
                """, ("$world", worldId), ("$plot", plotId), ("$house", houseId), ("$version", previousVersion + 1),
                ("$x", cellX.Value), ("$y", cellY.Value)))
                insert.ExecuteNonQuery();
            using (var assignment = Command("UPDATE assignments SET house_x=$x,house_y=$y WHERE world_id=$world AND plot_id=$plot",
                ("$x", cellX.Value), ("$y", cellY.Value), ("$world", worldId), ("$plot", plotId))) assignment.ExecuteNonQuery();
            using (var history = Command("""
                INSERT INTO plot_house_history(world_id,house_id,plot_id,house_version,previous_house_id)
                VALUES($world,$house,$plot,$version,$previous)
                """, ("$world", worldId), ("$house", houseId), ("$plot", plotId),
                ("$version", previousVersion + 1), ("$previous", previousHouseId)))
                history.ExecuteNonQuery();
            tx.Commit();
            return (true, "");
        }
    }

    public (bool Ok, string Error) MarkPlotHouseDestroyed(string worldId, string plotId, string owner,
        long generation, string houseId, long houseVersion, string blueprintId, string? nodeId = null, string? bootNonce = null)
    {
        if (!ValidSteamId(owner) || !ValidHouseId(houseId) || !ValidHouseId(blueprintId)
            || houseId == blueprintId || houseVersion <= 0) return (false, "invalid_house");
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            if ((nodeId != null || bootNonce != null) && !MapNodeAuthorized(worldId, generation, nodeId ?? "", bootNonce ?? ""))
                return (false, "node_identity_changed");
            if (OpenGeneration(worldId) != generation) return (false, "world_generation_changed");
            using (var history = Command("""
                SELECT COUNT(*) FROM plot_house_history WHERE world_id=$world
                  AND (house_id=$blueprint OR (blueprint_id=$blueprint AND house_id<>$house))
                """, ("$world", worldId), ("$blueprint", blueprintId), ("$house", houseId)))
                if (Convert.ToInt32(history.ExecuteScalar()) != 0) return (false, "house_id_reused");
            using var update = Command("""
                UPDATE plot_houses SET state='destroyed',blueprint_id=$blueprint
                WHERE world_id=$world AND plot_id=$plot AND house_id=$house AND house_version=$version
                  AND ((state='intact' AND blueprint_id='') OR (state='destroyed' AND blueprint_id=$blueprint)) AND EXISTS
                    (SELECT 1 FROM plot_owners WHERE world_id=$world AND plot_id=$plot AND steam_id=$owner)
                """, ("$world", worldId), ("$plot", plotId), ("$house", houseId), ("$version", houseVersion), ("$owner", owner), ("$blueprint", blueprintId));
            if (update.ExecuteNonQuery() != 1) return (false, "house_changed");
            using (var bind = Command("""
                UPDATE plot_house_history SET blueprint_id=$blueprint WHERE world_id=$world AND house_id=$house
                  AND plot_id=$plot AND house_version=$version AND blueprint_id IN ('',$blueprint)
                """, ("$world", worldId), ("$plot", plotId), ("$house", houseId), ("$version", houseVersion), ("$blueprint", blueprintId)))
                if (bind.ExecuteNonQuery() != 1) return (false, "house_changed");
            tx.Commit();
            return (true, "");
        }
    }

    public (bool Ok, string Error) CaptureDestroyedPlot(string worldId, string plotId, string previousOwner,
        string newOwner, long generation, string houseId, long houseVersion, string blueprintId, string eventId,
        string? nodeId = null, string? bootNonce = null)
    {
        if (!ValidSteamId(previousOwner) || !ValidSteamId(newOwner) || previousOwner == newOwner
            || !ValidHouseId(houseId) || !ValidHouseId(blueprintId) || houseId == blueprintId
            || houseVersion <= 0 || !ValidHouseId(eventId))
            return (false, "invalid_capture");
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            if ((nodeId != null || bootNonce != null) && !MapNodeAuthorized(worldId, generation, nodeId ?? "", bootNonce ?? ""))
                return (false, "node_identity_changed");
            if (OpenGeneration(worldId) != generation) return (false, "world_generation_changed");
            using (var replay = Command("""
                SELECT plot_id,previous_owner,new_owner,house_id,house_version,blueprint_id FROM plot_capture_events
                WHERE world_id=$world AND generation=$generation AND event_id=$event
                """, ("$world", worldId), ("$generation", generation), ("$event", eventId)))
            using (var reader = replay.ExecuteReader())
            {
                if (reader.Read())
                    return reader.GetString(0) == plotId && reader.GetString(1) == previousOwner && reader.GetString(2) == newOwner
                        && reader.GetString(3) == houseId && reader.GetInt64(4) == houseVersion && reader.GetString(5) == blueprintId
                        ? (true, "") : (false, "capture_event_conflict");
            }
            if (FindAssignment(worldId, newOwner) == null) return (false, "assignment_required");
            using (var house = Command("""
                UPDATE plot_houses SET state='claimed'
                WHERE world_id=$world AND plot_id=$plot AND house_id=$house AND house_version=$version AND blueprint_id=$blueprint AND state='destroyed'
                """, ("$world", worldId), ("$plot", plotId), ("$house", houseId), ("$version", houseVersion), ("$blueprint", blueprintId)))
                if (house.ExecuteNonQuery() != 1) return (false, "house_changed");
            using (var update = Command("""
                UPDATE plot_owners SET steam_id=$new
                WHERE world_id=$world AND plot_id=$plot AND steam_id=$previous
                """, ("$new", newOwner), ("$world", worldId), ("$plot", plotId), ("$previous", previousOwner)))
                if (update.ExecuteNonQuery() != 1) return (false, "plot_owner_changed");
            using (var record = Command("""
                INSERT INTO plot_capture_events(world_id,generation,event_id,plot_id,previous_owner,new_owner,house_id,house_version,blueprint_id)
                VALUES($world,$generation,$event,$plot,$previous,$new,$house,$version,$blueprint)
                """, ("$world", worldId), ("$generation", generation), ("$event", eventId),
                ("$plot", plotId), ("$previous", previousOwner), ("$new", newOwner),
                ("$house", houseId), ("$version", houseVersion), ("$blueprint", blueprintId))) record.ExecuteNonQuery();
            Audit("plot_capture", $"{worldId}/{plotId}/{previousOwner}/{newOwner}");
            tx.Commit();
            return (true, "");
        }
    }

    public bool Heartbeat(string id, string name, string region, string url, string version, int players)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            // Legacy reports contain no world identity. Once a world uses protected
            // boot identities, none can be safely attributed to it.
            using (var guard = Command("SELECT COUNT(*) FROM worlds WHERE boot_nonce <> ''"))
                if (Convert.ToInt64(guard.ExecuteScalar()) != 0) return false;
            using var command = Command("""
                INSERT INTO nodes(id,name,region,url,version,players,last_seen)
                VALUES($id,$name,$region,$url,$version,$players,$at)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,region=excluded.region,url=excluded.url,
                  version=excluded.version,players=excluded.players,last_seen=excluded.last_seen
                """, ("$id", id), ("$name", name), ("$region", region), ("$url", url),
                ("$version", version), ("$players", Math.Max(0, players)), ("$at", Now));
            command.ExecuteNonQuery();
            tx.Commit();
            return true;
        }
    }

    public IReadOnlyList<MultiplayerNode> Nodes()
    {
        lock (_gate)
        {
            using var command = Command("SELECT id,name,region,url,version,players,last_seen FROM nodes ORDER BY id");
            using var reader = command.ExecuteReader();
            var list = new List<MultiplayerNode>();
            while (reader.Read())
                list.Add(new MultiplayerNode(reader.GetString(0), reader.GetString(1), reader.GetString(2),
                    reader.GetString(3), reader.GetString(4), reader.GetInt32(5), reader.GetInt64(6)));
            return list;
        }
    }

    // 心跳与事件批次一起提交；收到确认后节点才会从发送队列移除事件。
    public bool MonitorHeartbeat(string id, string name, string region, string url, string version,
        string world, string[] steamIds, NodeConnectionEvent[] events, long generation, string bootNonce)
    {
        lock (_gate)
        {
            using var tx = _db.BeginTransaction(deferred: false);
            SqliteCommand InTx(string sql, params (string Key, object Value)[] values)
            {
                var command = Command(sql, values);
                command.Transaction = tx;
                return command;
            }
            using (var guard = InTx("SELECT generation,boot_nonce FROM worlds WHERE id=$world", ("$world", world)))
            using (var reader = guard.ExecuteReader())
            {
                if (!reader.Read()) return false;
                long expectedGeneration = reader.GetInt64(0);
                string expectedNonce = reader.GetString(1);
                if (expectedNonce.Length == 0)
                {
                    if (generation != 0 || bootNonce.Length != 0) return false;
                }
                else if (generation != expectedGeneration || bootNonce != expectedNonce) return false;
            }
            using (var guard = InTx("SELECT world_id,boot_nonce FROM node_monitor_state WHERE node_id=$node", ("$node", id)))
            using (var reader = guard.ExecuteReader())
                if (reader.Read() && reader.GetString(1).Length > 0
                    && (reader.GetString(0) != world || reader.GetString(1) != bootNonce)) return false;
            long now = Now;
            foreach (var ev in events)
            {
                using var existing = InTx("SELECT node_id,world_id,steam_id,type,at FROM node_connections WHERE id=$id", ("$id", ev.Id));
                using var reader = existing.ExecuteReader();
                if (reader.Read() && (reader.GetString(0) != id || reader.GetString(1) != world || reader.GetString(2) != ev.SteamId
                    || reader.GetString(3) != ev.Type || reader.GetInt64(4) != ev.At)) return false;
            }
            using (var command = InTx("""
                INSERT INTO nodes(id,name,region,url,version,players,last_seen) VALUES($id,$name,$region,$url,$version,$players,$at)
                ON CONFLICT(id) DO UPDATE SET name=excluded.name,region=excluded.region,url=excluded.url,
                  version=excluded.version,players=excluded.players,last_seen=excluded.last_seen
                """, ("$id", id), ("$name", name), ("$region", region), ("$url", url),
                ("$version", version), ("$players", steamIds.Length), ("$at", now))) command.ExecuteNonQuery();
            using (var command = InTx("""
                INSERT INTO node_monitor_state(node_id,world_id,last_seen,generation,boot_nonce) VALUES($node,$world,$at,$generation,$nonce)
                ON CONFLICT(node_id) DO UPDATE SET world_id=excluded.world_id,last_seen=excluded.last_seen,
                  generation=excluded.generation,boot_nonce=excluded.boot_nonce
                """, ("$node", id), ("$world", world), ("$at", now), ("$generation", generation), ("$nonce", bootNonce))) command.ExecuteNonQuery();
            using (var command = InTx("DELETE FROM node_online WHERE node_id=$node", ("$node", id))) command.ExecuteNonQuery();
            foreach (string steam in steamIds)
                using (var command = InTx("INSERT INTO node_online(node_id,steam_id) VALUES($node,$steam)", ("$node", id), ("$steam", steam))) command.ExecuteNonQuery();
            foreach (var ev in events)
                using (var command = InTx("INSERT OR IGNORE INTO node_connections(id,node_id,world_id,steam_id,type,at,received) VALUES($id,$node,$world,$steam,$type,$at,$received)",
                    ("$id", ev.Id), ("$node", id), ("$world", world), ("$steam", ev.SteamId), ("$type", ev.Type), ("$at", ev.At), ("$received", now))) command.ExecuteNonQuery();
            tx.Commit();
            return true;
        }
    }

    public object MonitorData()
    {
        lock (_gate)
        {
            var snapshots = new List<object>();
            using (var command = Command("SELECT node_id,world_id,last_seen FROM node_monitor_state ORDER BY node_id"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) snapshots.Add(new { node_id = reader.GetString(0), world_id = reader.GetString(1), last_seen = reader.GetInt64(2) });
            var online = new List<object>();
            using (var command = Command("SELECT node_id,steam_id FROM node_online ORDER BY node_id,steam_id"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) online.Add(new { node_id = reader.GetString(0), steam_id = reader.GetString(1) });
            var events = new List<object>();
            using (var command = Command("SELECT node_id,world_id,steam_id,type,at,received FROM node_connections ORDER BY received DESC,rowid DESC LIMIT 100"))
            using (var reader = command.ExecuteReader())
                while (reader.Read()) events.Add(new { node_id = reader.GetString(0), world_id = reader.GetString(1), steam_id = reader.GetString(2),
                    type = reader.GetString(3), at = reader.GetInt64(4), received = reader.GetInt64(5) });
            return new { snapshots, online, events };
        }
    }

    public IReadOnlyList<MultiplayerAudit> Audit(int limit = 100)
    {
        lock (_gate)
        {
            using var command = Command("SELECT action,target,at FROM audit ORDER BY id DESC LIMIT $limit",
                ("$limit", Math.Clamp(limit, 1, 500)));
            using var reader = command.ExecuteReader();
            var list = new List<MultiplayerAudit>();
            while (reader.Read()) list.Add(new MultiplayerAudit(reader.GetString(0), reader.GetString(1), reader.GetInt64(2)));
            return list;
        }
    }

    private void Audit(string action, string target)
    {
        using var command = Command("INSERT INTO audit(action,target,at) VALUES($action,$target,$at)",
            ("$action", action), ("$target", target), ("$at", Now));
        command.ExecuteNonQuery();
    }

    public void Dispose() => _db.Dispose();
}

public sealed record MultiplayerWorld(string Id, string Name, string MapId, string Status, int Capacity, bool Ranked, int Players);
public sealed record MultiplayerAssignment(string WorldId, string SteamId, string PlotId, string SpawnId,
    string HouseSeed, int HouseX, int HouseY, long Created, long MapGeneration);
public sealed record MultiplayerNode(string Id, string Name, string Region, string Url, string Version, int Players, long LastSeen);
public sealed record PlotHouseSnapshot(string PlotId, string OwnerSteamId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? HouseId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] long? HouseVersion,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? State,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] string? BlueprintId,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] int? HouseX,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.Never)] int? HouseY);
public sealed record MultiplayerAudit(string Action, string Target, long At);
public sealed record WorldResetStatusRecord(long WorkerLastSeen, string JobId, string Phase, string Error, long Created, long Updated);
public sealed record NodeConnectionEvent(string Id, string Type, string SteamId, long At);
