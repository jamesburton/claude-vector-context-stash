using CCStash.Core.Embeddings;
using CCStash.Core.Storage;

namespace CCStash.Core;

/// <summary>A retrieved chunk projected for presentation.</summary>
public sealed record RetrievedChunk(string Text, int TurnIndex, string Role, DateTimeOffset? Timestamp, float Score);

/// <summary>Embeds a query and returns the nearest stored chunks.</summary>
public interface IRetrievalService
{
    /// <summary>Retrieve up to <paramref name="limit"/> chunks, optionally scoped to a session.</summary>
    Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string query, int limit, string? session, CancellationToken ct = default);
}

/// <inheritdoc cref="IRetrievalService"/>
public sealed class RetrievalService(IEmbedder embedder, IVectorStore store) : IRetrievalService
{
    /// <inheritdoc/>
    public async Task<IReadOnlyList<RetrievedChunk>> RetrieveAsync(string query, int limit, string? session, CancellationToken ct = default)
    {
        await store.InitializeAsync(embedder.Dimension, embedder.ModelId, ct);
        var q = await embedder.EmbedAsync(query, ct);
        var hits = await store.SearchAsync(q, limit, session, query, ct);
        return hits
            .Select(h => new RetrievedChunk(h.Chunk.Text, h.Chunk.TurnIndex, h.Chunk.Role, h.Chunk.Timestamp, h.Score))
            .ToList();
    }
}
