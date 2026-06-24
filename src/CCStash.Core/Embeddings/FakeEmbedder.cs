namespace CCStash.Core.Embeddings;

/// <summary>Deterministic hashing embedder for tests and offline use.</summary>
public sealed class FakeEmbedder(int dimension = 8) : IEmbedder
{
    /// <inheritdoc/>
    public int Dimension { get; } = dimension;

    /// <inheritdoc/>
    public string ModelId => $"fake-{Dimension}";

    /// <inheritdoc/>
    public Task<float[]> EmbedAsync(string text, CancellationToken ct = default)
    {
        var v = new float[Dimension];
        foreach (var token in text.Split(' ', StringSplitOptions.RemoveEmptyEntries))
        {
            v[(uint)token.GetHashCode(StringComparison.Ordinal) % Dimension] += 1f;
        }

        var norm = MathF.Sqrt(v.Sum(x => x * x));
        if (norm > 0)
        {
            for (var i = 0; i < v.Length; i++)
            {
                v[i] /= norm;
            }
        }

        return Task.FromResult(v);
    }

    /// <inheritdoc/>
    public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
    {
        var result = new List<float[]>(texts.Count);
        foreach (var t in texts)
        {
            result.Add(await EmbedAsync(t, ct));
        }

        return result;
    }
}
