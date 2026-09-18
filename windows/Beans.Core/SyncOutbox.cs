using Microsoft.Data.Sqlite;

namespace Beans.Core;

public sealed record SyncAccountState(long Cursor, bool Seeded);
public sealed record LocalSyncRecord(string EntityType, string EntityId, long Revision, bool Deleted, string Ciphertext, DateTimeOffset UpdatedAt);

/// <summary>SQLite 同步存储；正文始终以保险库密钥加密。</summary>
public sealed class SyncOutbox(string databasePath)
{
    private readonly string _connectionString = new SqliteConnectionStringBuilder { DataSource = databasePath }.ToString();

    public async Task InitializeAsync(CancellationToken ct = default)
    {
        var directory = Path.GetDirectoryName(databasePath);
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        await using var db = await OpenAsync(ct);
        var sql = db.CreateCommand();
        sql.CommandText = """
            CREATE TABLE IF NOT EXISTS sync_state (user_id TEXT NOT NULL, entity_type TEXT NOT NULL, entity_id TEXT NOT NULL, revision INTEGER NOT NULL DEFAULT 0, PRIMARY KEY(user_id,entity_type,entity_id));
            CREATE TABLE IF NOT EXISTS sync_outbox (id TEXT PRIMARY KEY, user_id TEXT NOT NULL, entity_type TEXT NOT NULL, entity_id TEXT NOT NULL, base_revision INTEGER NOT NULL, deleted INTEGER NOT NULL, ciphertext TEXT NOT NULL, updated_at TEXT NOT NULL, UNIQUE(user_id,entity_type,entity_id));
            CREATE TABLE IF NOT EXISTS sync_account (user_id TEXT PRIMARY KEY, cursor INTEGER NOT NULL DEFAULT 0, seeded INTEGER NOT NULL DEFAULT 0);
            CREATE TABLE IF NOT EXISTS sync_mirror (user_id TEXT NOT NULL, entity_type TEXT NOT NULL, entity_id TEXT NOT NULL, revision INTEGER NOT NULL, deleted INTEGER NOT NULL, ciphertext TEXT NOT NULL, updated_at TEXT NOT NULL, PRIMARY KEY(user_id,entity_type,entity_id));
            """;
        await sql.ExecuteNonQueryAsync(ct);
    }

    public async Task<SyncAccountState> AccountStateAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await EnsureAccountAsync(db, userId, null, ct);
        var sql = db.CreateCommand();
        sql.CommandText = "SELECT cursor,seeded FROM sync_account WHERE user_id=$user";
        sql.Parameters.AddWithValue("$user", userId.ToString());
        await using var row = await sql.ExecuteReaderAsync(ct);
        await row.ReadAsync(ct);
        return new(row.GetInt64(0), row.GetInt64(1) != 0);
    }

    public async Task MarkSeededAsync(Guid userId, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await EnsureAccountAsync(db, userId, null, ct);
        var sql = db.CreateCommand();
        sql.CommandText = "UPDATE sync_account SET seeded=1 WHERE user_id=$user";
        sql.Parameters.AddWithValue("$user", userId.ToString());
        await sql.ExecuteNonQueryAsync(ct);
    }

    public async Task EnqueueAsync(Guid userId, string entityType, string entityId, bool deleted, byte[] ciphertext, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await EnsureAccountAsync(db, userId, null, ct);
        var sql = db.CreateCommand();
        sql.CommandText = """
            INSERT INTO sync_outbox(id,user_id,entity_type,entity_id,base_revision,deleted,ciphertext,updated_at)
            VALUES($id,$user,$type,$entity,COALESCE((SELECT revision FROM sync_state WHERE user_id=$user AND entity_type=$type AND entity_id=$entity),0),$deleted,$ciphertext,$updated)
            ON CONFLICT(user_id,entity_type,entity_id) DO UPDATE SET id=excluded.id,base_revision=excluded.base_revision,deleted=excluded.deleted,ciphertext=excluded.ciphertext,updated_at=excluded.updated_at
            """;
        sql.Parameters.AddWithValue("$id", Guid.NewGuid().ToString());
        sql.Parameters.AddWithValue("$user", userId.ToString());
        sql.Parameters.AddWithValue("$type", entityType);
        sql.Parameters.AddWithValue("$entity", entityId);
        sql.Parameters.AddWithValue("$deleted", deleted ? 1 : 0);
        sql.Parameters.AddWithValue("$ciphertext", Convert.ToBase64String(ciphertext));
        sql.Parameters.AddWithValue("$updated", DateTimeOffset.UtcNow.ToString("O"));
        await sql.ExecuteNonQueryAsync(ct);
    }

    public async Task<IReadOnlyList<SyncEnvelope>> PendingAsync(Guid userId, Guid deviceId, int limit = 200, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var sql = db.CreateCommand();
        sql.CommandText = "SELECT id,entity_type,entity_id,base_revision,deleted,ciphertext,updated_at FROM sync_outbox WHERE user_id=$user ORDER BY updated_at LIMIT $limit";
        sql.Parameters.AddWithValue("$user", userId.ToString());
        sql.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 200));
        var result = new List<SyncEnvelope>();
        await using var rows = await sql.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct))
            result.Add(new(Guid.Parse(rows.GetString(0)), rows.GetString(1), rows.GetString(2), deviceId, rows.GetInt64(3), 0, rows.GetInt64(4) != 0, rows.GetString(5), DateTimeOffset.Parse(rows.GetString(6))));
        return result;
    }

    public async Task IngestAsync(Guid userId, SyncPage page, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);
        await EnsureAccountAsync(db, userId, tx, ct);
        foreach (var record in page.Records)
        {
            if (record.Revision <= await KnownRevisionAsync(db, userId, record, tx, ct)) continue;
            var sql = db.CreateCommand();
            sql.Transaction = tx;
            sql.CommandText = """
                INSERT INTO sync_state(user_id,entity_type,entity_id,revision) VALUES($user,$type,$entity,$revision) ON CONFLICT(user_id,entity_type,entity_id) DO UPDATE SET revision=excluded.revision;
                INSERT INTO sync_mirror(user_id,entity_type,entity_id,revision,deleted,ciphertext,updated_at) VALUES($user,$type,$entity,$revision,$deleted,$ciphertext,$updated) ON CONFLICT(user_id,entity_type,entity_id) DO UPDATE SET revision=excluded.revision,deleted=excluded.deleted,ciphertext=excluded.ciphertext,updated_at=excluded.updated_at;
                UPDATE sync_outbox SET base_revision=$revision WHERE user_id=$user AND entity_type=$type AND entity_id=$entity
                """;
            AddRecord(sql, userId, record);
            await sql.ExecuteNonQueryAsync(ct);
        }
        var cursor = db.CreateCommand();
        cursor.Transaction = tx;
        cursor.CommandText = "UPDATE sync_account SET cursor=MAX(cursor,$cursor) WHERE user_id=$user";
        cursor.Parameters.AddWithValue("$cursor", page.Cursor);
        cursor.Parameters.AddWithValue("$user", userId.ToString());
        await cursor.ExecuteNonQueryAsync(ct);
        await tx.CommitAsync(ct);
    }

    public async Task AcknowledgeAsync(Guid userId, IReadOnlyList<SyncEnvelope> records, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        await using var tx = (SqliteTransaction)await db.BeginTransactionAsync(ct);
        await EnsureAccountAsync(db, userId, tx, ct);
        foreach (var record in records)
        {
            var sql = db.CreateCommand();
            sql.Transaction = tx;
            sql.CommandText = """
                DELETE FROM sync_outbox WHERE user_id=$user AND id=$id;
                INSERT INTO sync_state(user_id,entity_type,entity_id,revision) VALUES($user,$type,$entity,$revision) ON CONFLICT(user_id,entity_type,entity_id) DO UPDATE SET revision=MAX(revision,excluded.revision);
                INSERT INTO sync_mirror(user_id,entity_type,entity_id,revision,deleted,ciphertext,updated_at) VALUES($user,$type,$entity,$revision,$deleted,$ciphertext,$updated) ON CONFLICT(user_id,entity_type,entity_id) DO UPDATE SET revision=excluded.revision,deleted=excluded.deleted,ciphertext=excluded.ciphertext,updated_at=excluded.updated_at;
                UPDATE sync_account SET cursor=MAX(cursor,$revision) WHERE user_id=$user
                """;
            AddRecord(sql, userId, record);
            sql.Parameters.AddWithValue("$id", record.Id.ToString());
            await sql.ExecuteNonQueryAsync(ct);
        }
        await tx.CommitAsync(ct);
    }

    public async Task<IReadOnlyList<LocalSyncRecord>> ReadMirrorAsync(Guid userId, string entityType, CancellationToken ct = default)
    {
        await using var db = await OpenAsync(ct);
        var sql = db.CreateCommand();
        sql.CommandText = "SELECT entity_type,entity_id,revision,deleted,ciphertext,updated_at FROM sync_mirror WHERE user_id=$user AND entity_type=$type";
        sql.Parameters.AddWithValue("$user", userId.ToString());
        sql.Parameters.AddWithValue("$type", entityType);
        var result = new List<LocalSyncRecord>();
        await using var rows = await sql.ExecuteReaderAsync(ct);
        while (await rows.ReadAsync(ct)) result.Add(new(rows.GetString(0), rows.GetString(1), rows.GetInt64(2), rows.GetInt64(3) != 0, rows.GetString(4), DateTimeOffset.Parse(rows.GetString(5))));
        return result;
    }

    private async Task<SqliteConnection> OpenAsync(CancellationToken ct) { var db = new SqliteConnection(_connectionString); await db.OpenAsync(ct); return db; }

    private static async Task EnsureAccountAsync(SqliteConnection db, Guid userId, SqliteTransaction? tx, CancellationToken ct)
    {
        var sql = db.CreateCommand(); sql.Transaction = tx;
        sql.CommandText = "INSERT OR IGNORE INTO sync_account(user_id,cursor,seeded) VALUES($user,0,0)";
        sql.Parameters.AddWithValue("$user", userId.ToString()); await sql.ExecuteNonQueryAsync(ct);
    }

    private static async Task<long> KnownRevisionAsync(SqliteConnection db, Guid userId, SyncEnvelope record, SqliteTransaction tx, CancellationToken ct)
    {
        var sql = db.CreateCommand(); sql.Transaction = tx;
        sql.CommandText = "SELECT revision FROM sync_state WHERE user_id=$user AND entity_type=$type AND entity_id=$entity";
        sql.Parameters.AddWithValue("$user", userId.ToString()); sql.Parameters.AddWithValue("$type", record.EntityType); sql.Parameters.AddWithValue("$entity", record.EntityId);
        return Convert.ToInt64(await sql.ExecuteScalarAsync(ct) ?? 0L);
    }

    private static void AddRecord(SqliteCommand sql, Guid userId, SyncEnvelope record)
    {
        sql.Parameters.AddWithValue("$user", userId.ToString()); sql.Parameters.AddWithValue("$type", record.EntityType); sql.Parameters.AddWithValue("$entity", record.EntityId);
        sql.Parameters.AddWithValue("$revision", record.Revision); sql.Parameters.AddWithValue("$deleted", record.Deleted ? 1 : 0); sql.Parameters.AddWithValue("$ciphertext", record.Ciphertext); sql.Parameters.AddWithValue("$updated", record.UpdatedAt.ToString("O"));
    }
}
