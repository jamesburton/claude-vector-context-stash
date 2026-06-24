namespace CCStash.Core.Storage;

/// <summary>In-memory cosine-similarity store used for tests and offline runs.</summary>
public sealed class InMemoryVectorStore : IVectorStore
{
    private readonly Dictionary<string, StoredChunk> _chunks = new();

    /// <inheritdoc/>
    public Task InitializeAsync(int dimension, string embeddingModel, CancellationToken ct = default)
        => Task.CompletedTask;

    /// <inheritdoc/>
    public Task UpsertAsync(IReadOnlyList<StoredChunk> chunks, CancellationToken ct = default)
    {
        foreach (var c in chunks)
        {
            _chunks[c.Id] = c;
        }

        return Task.CompletedTask;
    }

    /// <inheritdoc/>
    public Task<IReadOnlyList<SearchHit>> SearchAsync(float[] query, int limit, string? session, CancellationToken ct = default)
    {
        IReadOnlyList<SearchHit> hits = _chunks.Values
            .Where(c => session is null || c.Session == session)
            .Select(c => new SearchHit(c, Cosine(query, c.Embedding)))
            .OrderByDescending(h => h.Score)
            .Take(limit)
            .ToList();

        return Task.FromResult(hits);
    }

    /// <inheritdoc/>
    public Task<int> CountAsync(string? session, CancellationToken ct = default)
        => Task.FromResult(_chunks.Values.Count(c => session is null || c.Session == session));

    /// <inheritdoc/>
    public Task<int> GetHighWaterMarkAsync(string session, CancellationToken ct = default)
    {
        var forSession = _chunks.Values.Where(c => c.Session == session).ToList();
        return Task.FromResult(forSession.Count == 0 ? -1 : forSession.Max(c => c.TurnIndex));
    }

    /// <inheritdoc/>
    public Task<string?> GetLatestSessionAsync(CancellationToken ct = default)
        => Task.FromResult(_chunks.Values
            .OrderByDescending(c => c.Timestamp ?? DateTimeOffset.MinValue)
            .Select(c => c.Session)
            .FirstOrDefault());

    /// <summary>No-op; nothing to release.</summary>
    public void Dispose()
    {
    }

    private static float Cosine(float[] a, float[] b)
    {
        float dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Length; i++)
        {
            dot += a[i] * b[i];
            na += a[i] * a[i];
            nb += b[i] * b[i];
        }

        return na == 0 || nb == 0 ? 0 : dot / (MathF.Sqrt(na) * MathF.Sqrt(nb));
    }
}
