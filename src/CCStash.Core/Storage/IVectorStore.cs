namespace CCStash.Core.Storage;

/// <summary>An embedded vector store for distilled conversation chunks.</summary>
public interface IVectorStore : IDisposable
{
    /// <summary>Ensure the store exists and matches the given embedding dimension/model.</summary>
    Task InitializeAsync(int dimension, string embeddingModel, CancellationToken ct = default);

    /// <summary>Insert or replace chunks by <see cref="StoredChunk.Id"/>.</summary>
    Task UpsertAsync(IReadOnlyList<StoredChunk> chunks, CancellationToken ct = default);

    /// <summary>
    /// Return the nearest chunks to <paramref name="query"/>, optionally limited to one session.
    /// When <paramref name="queryText"/> is supplied, stores that support it may blend keyword
    /// relevance with vector similarity (hybrid search); others ignore it.
    /// </summary>
    Task<IReadOnlyList<SearchHit>> SearchAsync(float[] query, int limit, string? session, string? queryText = null, CancellationToken ct = default);

    /// <summary>Count chunks, optionally for a single session.</summary>
    Task<int> CountAsync(string? session, CancellationToken ct = default);

    /// <summary>Highest stored turn index for a session, or -1 if none.</summary>
    Task<int> GetHighWaterMarkAsync(string session, CancellationToken ct = default);

    /// <summary>The session with the most recently stored chunk, or null if empty.</summary>
    Task<string?> GetLatestSessionAsync(CancellationToken ct = default);
}
