using System.Globalization;
using CCStash.Core.Storage;
using Microsoft.Data.Sqlite;

namespace CCStash.Stores.Sqlite;

/// <summary>
/// A single-file SQLite-backed <see cref="IVectorStore"/>. Vectors are stored as BLOBs and
/// ranked with managed cosine similarity. This is the plan's documented contingency for
/// environments where the native <c>vec0</c> extension is unavailable; it satisfies the
/// architecture (embedded, single file, no daemon) and is ample at single-developer scale.
/// Swapping in the sqlite-vec virtual table later is isolated to this class.
/// </summary>
public sealed class SqliteVectorStore(string dbPath) : IVectorStore
{
    private SqliteConnection? _connection;

    private SqliteConnection Conn => _connection ?? throw new InvalidOperationException("InitializeAsync not called.");

    /// <inheritdoc/>
    public async Task InitializeAsync(int dimension, string embeddingModel, CancellationToken ct = default)
    {
        if (_connection is not null)
        {
            return;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(dbPath)!);
        // Pooling=False so the file handle is released on Dispose — CCStash opens a fresh
        // connection per CLI invocation, so the pool would only keep the db file locked.
        _connection = new SqliteConnection($"Data Source={dbPath};Pooling=False");
        await _connection.OpenAsync(ct);

        await Exec(
            """
            CREATE TABLE IF NOT EXISTS chunks(
                id TEXT PRIMARY KEY, project TEXT, session TEXT, turn_index INTEGER,
                role TEXT, type TEXT, ts TEXT, text TEXT, embedding BLOB);
            CREATE INDEX IF NOT EXISTS ix_chunks_session ON chunks(session);
            """, ct);
    }

    /// <inheritdoc/>
    public async Task UpsertAsync(IReadOnlyList<StoredChunk> chunks, CancellationToken ct = default)
    {
        await using var tx = await Conn.BeginTransactionAsync(ct);
        foreach (var c in chunks)
        {
            await using var cmd = Conn.CreateCommand();
            cmd.CommandText =
                """
                INSERT INTO chunks(id,project,session,turn_index,role,type,ts,text,embedding)
                VALUES($id,$p,$s,$t,$r,$ty,$ts,$txt,$emb)
                ON CONFLICT(id) DO UPDATE SET
                    project=$p,session=$s,turn_index=$t,role=$r,type=$ty,ts=$ts,text=$txt,embedding=$emb;
                """;
            cmd.Parameters.AddWithValue("$id", c.Id);
            cmd.Parameters.AddWithValue("$p", c.Project);
            cmd.Parameters.AddWithValue("$s", c.Session);
            cmd.Parameters.AddWithValue("$t", c.TurnIndex);
            cmd.Parameters.AddWithValue("$r", c.Role);
            cmd.Parameters.AddWithValue("$ty", c.Type);
            cmd.Parameters.AddWithValue("$ts", c.Timestamp?.ToString("O", CultureInfo.InvariantCulture) ?? string.Empty);
            cmd.Parameters.AddWithValue("$txt", c.Text);
            cmd.Parameters.AddWithValue("$emb", ToBlob(c.Embedding));
            await cmd.ExecuteNonQueryAsync(ct);
        }

        await tx.CommitAsync(ct);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(float[] query, int limit, string? session, CancellationToken ct = default)
    {
        await using var cmd = Conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT id,project,session,turn_index,role,type,ts,text,embedding
            FROM chunks WHERE ($session IS NULL OR session = $session);
            """;
        cmd.Parameters.AddWithValue("$session", (object?)session ?? DBNull.Value);

        var hits = new List<SearchHit>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        while (await reader.ReadAsync(ct))
        {
            var embedding = FromBlob((byte[])reader["embedding"]);
            hits.Add(new SearchHit(ReadChunk(reader, embedding), Cosine(query, embedding)));
        }

        return hits.OrderByDescending(h => h.Score).Take(limit).ToList();
    }

    /// <inheritdoc/>
    public async Task<int> CountAsync(string? session, CancellationToken ct = default)
    {
        await using var cmd = Conn.CreateCommand();
        cmd.CommandText = "SELECT COUNT(*) FROM chunks WHERE ($s IS NULL OR session=$s);";
        cmd.Parameters.AddWithValue("$s", (object?)session ?? DBNull.Value);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public async Task<int> GetHighWaterMarkAsync(string session, CancellationToken ct = default)
    {
        await using var cmd = Conn.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(MAX(turn_index), -1) FROM chunks WHERE session=$s;";
        cmd.Parameters.AddWithValue("$s", session);
        return Convert.ToInt32(await cmd.ExecuteScalarAsync(ct), CultureInfo.InvariantCulture);
    }

    /// <inheritdoc/>
    public async Task<string?> GetLatestSessionAsync(CancellationToken ct = default)
    {
        await using var cmd = Conn.CreateCommand();
        cmd.CommandText = "SELECT session FROM chunks ORDER BY ts DESC LIMIT 1;";
        return await cmd.ExecuteScalarAsync(ct) as string;
    }

    /// <summary>Close the underlying connection.</summary>
    public void Dispose() => _connection?.Dispose();

    private static byte[] ToBlob(float[] v)
    {
        var bytes = new byte[v.Length * sizeof(float)];
        Buffer.BlockCopy(v, 0, bytes, 0, bytes.Length);
        return bytes;
    }

    private static float[] FromBlob(byte[] bytes)
    {
        var v = new float[bytes.Length / sizeof(float)];
        Buffer.BlockCopy(bytes, 0, v, 0, bytes.Length);
        return v;
    }

    private static float Cosine(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        var n = Math.Min(a.Length, b.Length);
        for (var i = 0; i < n; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }

    private static StoredChunk ReadChunk(SqliteDataReader r, float[] embedding)
    {
        var ts = r.GetString(6);
        return new StoredChunk(
            r.GetString(0), r.GetString(1), r.GetString(2), r.GetInt32(3),
            r.GetString(4), r.GetString(5),
            string.IsNullOrEmpty(ts) ? null : DateTimeOffset.Parse(ts, CultureInfo.InvariantCulture),
            r.GetString(7), embedding);
    }

    private async Task Exec(string sql, CancellationToken ct)
    {
        await using var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync(ct);
    }
}
