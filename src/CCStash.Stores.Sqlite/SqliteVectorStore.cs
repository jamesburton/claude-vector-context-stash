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

        // The MCP server holds a connection for its lifetime while separate stash/pointer
        // processes write the same file. WAL allows concurrent reader+writer, and busy_timeout
        // makes a writer wait briefly for a lock instead of failing with SQLITE_BUSY.
        await Exec("PRAGMA journal_mode=WAL; PRAGMA busy_timeout=5000;", ct);

        await Exec(
            """
            CREATE TABLE IF NOT EXISTS chunks(
                id TEXT PRIMARY KEY, project TEXT, session TEXT, turn_index INTEGER,
                role TEXT, type TEXT, ts TEXT, text TEXT, embedding BLOB);
            CREATE INDEX IF NOT EXISTS ix_chunks_session ON chunks(session);
            CREATE TABLE IF NOT EXISTS meta(key TEXT PRIMARY KEY, value TEXT);
            CREATE VIRTUAL TABLE IF NOT EXISTS chunks_fts USING fts5(id UNINDEXED, text);
            """, ct);

        await GuardEmbeddingModelAsync(dimension, embeddingModel, ct);
    }

    /// <summary>
    /// Vectors from different models/dimensions are not comparable. If the recorded model or
    /// dimension differs from the current embedder, wipe the (now-unusable) chunks and record the
    /// new identity, so a fresh re-stash repopulates cleanly rather than mixing vector spaces.
    /// </summary>
    private async Task GuardEmbeddingModelAsync(int dimension, string embeddingModel, CancellationToken ct)
    {
        var storedModel = await ScalarAsync("SELECT value FROM meta WHERE key='model';", ct);
        var storedDim = await ScalarAsync("SELECT value FROM meta WHERE key='dimension';", ct);
        var current = $"{embeddingModel}/{dimension}";

        if (storedModel is not null && $"{storedModel}/{storedDim}" != current)
        {
            await Exec("DELETE FROM chunks; DELETE FROM chunks_fts;", ct);
        }

        await Exec(
            "INSERT INTO meta(key,value) VALUES('model',$m),('dimension',$d) " +
            "ON CONFLICT(key) DO UPDATE SET value=excluded.value;",
            ct,
            ("$m", embeddingModel),
            ("$d", dimension.ToString(CultureInfo.InvariantCulture)));
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
                DELETE FROM chunks_fts WHERE id=$id;
                INSERT INTO chunks_fts(id,text) VALUES($id,$txt);
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
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(float[] query, int limit, string? session, string? queryText = null, CancellationToken ct = default)
    {
        // Vector pass: cosine over every (session-scoped) chunk. Keeps cosine for all candidates.
        var candidates = new List<(StoredChunk Chunk, float Cosine)>();
        await using (var cmd = Conn.CreateCommand())
        {
            cmd.CommandText =
                """
                SELECT id,project,session,turn_index,role,type,ts,text,embedding
                FROM chunks WHERE ($session IS NULL OR session = $session);
                """;
            cmd.Parameters.AddWithValue("$session", (object?)session ?? DBNull.Value);
            await using var reader = await cmd.ExecuteReaderAsync(ct);
            while (await reader.ReadAsync(ct))
            {
                var embedding = FromBlob((byte[])reader["embedding"]);
                candidates.Add((ReadChunk(reader, embedding), Cosine(query, embedding)));
            }
        }

        if (candidates.Count == 0)
        {
            return [];
        }

        var cosineById = candidates.ToDictionary(c => c.Chunk.Id, c => c.Cosine);

        // Rank by vector similarity (1-based).
        var vecRank = candidates
            .OrderByDescending(c => c.Cosine)
            .Select((c, i) => (c.Chunk.Id, Rank: i + 1))
            .ToDictionary(x => x.Id, x => x.Rank);

        // Keyword pass (FTS5/BM25). If there are no usable terms, fall back to pure vector ranking.
        var ftsRank = await KeywordRankAsync(queryText, session, ct);
        if (ftsRank.Count == 0)
        {
            return candidates
                .OrderByDescending(c => c.Cosine)
                .Take(limit)
                .Select(c => new SearchHit(c.Chunk, c.Cosine))
                .ToList();
        }

        // Reciprocal Rank Fusion across the two rankings; report cosine as the displayed score.
        const float K = 60f;
        var byId = candidates.ToDictionary(c => c.Chunk.Id, c => c.Chunk);
        var ids = vecRank.Keys.Union(ftsRank.Keys);
        return ids
            .Select(id => new
            {
                Id = id,
                Rrf = (vecRank.TryGetValue(id, out var vr) ? 1f / (K + vr) : 0f)
                    + (ftsRank.TryGetValue(id, out var fr) ? 1f / (K + fr) : 0f),
            })
            .OrderByDescending(x => x.Rrf)
            .Take(limit)
            .Where(x => byId.ContainsKey(x.Id))
            .Select(x => new SearchHit(byId[x.Id], cosineById[x.Id]))
            .ToList();
    }

    private async Task<Dictionary<string, int>> KeywordRankAsync(string? queryText, string? session, CancellationToken ct)
    {
        var match = BuildMatchQuery(queryText);
        if (match is null)
        {
            return [];
        }

        await using var cmd = Conn.CreateCommand();
        cmd.CommandText =
            """
            SELECT f.id FROM chunks_fts f
            JOIN chunks c ON c.id = f.id
            WHERE f.text MATCH $q AND ($session IS NULL OR c.session = $session)
            ORDER BY bm25(chunks_fts) LIMIT 50;
            """;
        cmd.Parameters.AddWithValue("$q", match);
        cmd.Parameters.AddWithValue("$session", (object?)session ?? DBNull.Value);

        var ranks = new Dictionary<string, int>();
        await using var reader = await cmd.ExecuteReaderAsync(ct);
        var rank = 1;
        while (await reader.ReadAsync(ct))
        {
            ranks[reader.GetString(0)] = rank++;
        }

        return ranks;
    }

    /// <summary>Turn free text into a safe FTS5 OR-query of alphanumeric terms, or null if none.</summary>
    private static string? BuildMatchQuery(string? queryText)
    {
        if (string.IsNullOrWhiteSpace(queryText))
        {
            return null;
        }

        var terms = new List<string>();
        foreach (var raw in queryText.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries))
        {
            var token = new string(raw.Where(char.IsLetterOrDigit).ToArray());
            if (token.Length > 1)
            {
                terms.Add($"\"{token}\"");
            }
        }

        return terms.Count == 0 ? null : string.Join(" OR ", terms);
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

    private async Task Exec(string sql, CancellationToken ct, params (string Name, object Value)[] parameters)
    {
        await using var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        foreach (var (name, value) in parameters)
        {
            cmd.Parameters.AddWithValue(name, value);
        }

        await cmd.ExecuteNonQueryAsync(ct);
    }

    private async Task<string?> ScalarAsync(string sql, CancellationToken ct)
    {
        await using var cmd = Conn.CreateCommand();
        cmd.CommandText = sql;
        return await cmd.ExecuteScalarAsync(ct) as string;
    }
}
