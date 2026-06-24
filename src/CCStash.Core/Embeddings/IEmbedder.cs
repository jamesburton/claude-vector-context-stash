namespace CCStash.Core.Embeddings;

/// <summary>Produces dense vector embeddings for text.</summary>
public interface IEmbedder
{
    /// <summary>Embedding vector length.</summary>
    int Dimension { get; }

    /// <summary>Identifier of the embedding model (recorded in the store).</summary>
    string ModelId { get; }

    /// <summary>Embed a single string.</summary>
    Task<float[]> EmbedAsync(string text, CancellationToken ct = default);

    /// <summary>Embed many strings.</summary>
    Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default);
}
