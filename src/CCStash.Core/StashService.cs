using CCStash.Core.Chunking;
using CCStash.Core.Config;
using CCStash.Core.Distillation;
using CCStash.Core.Embeddings;
using CCStash.Core.Storage;
using CCStash.Core.Transcript;

namespace CCStash.Core;

/// <summary>Input describing what to stash.</summary>
public sealed record StashRequest(string TranscriptPath, string Project, string Session);

/// <summary>Outcome of a stash operation.</summary>
public sealed record StashResult(int NewChunks, int TotalChunks, string StashId);

/// <summary>Orchestrates parse → distill → chunk → embed → store, incrementally.</summary>
public interface IStashService
{
    /// <summary>Stash any turns newer than what is already stored for the session.</summary>
    Task<StashResult> StashAsync(StashRequest request, CancellationToken ct = default);
}

/// <inheritdoc cref="IStashService"/>
public sealed class StashService(
    ITranscriptParser parser,
    IDistiller distiller,
    IChunker chunker,
    IEmbedder embedder,
    IVectorStore store,
    CCStashConfig config) : IStashService
{
    /// <inheritdoc/>
    public async Task<StashResult> StashAsync(StashRequest request, CancellationToken ct = default)
    {
        await store.InitializeAsync(embedder.Dimension, embedder.ModelId, ct);

        var highWater = await store.GetHighWaterMarkAsync(request.Session, ct);
        var turns = parser.Parse(request.TranscriptPath).Where(t => t.Index > highWater).ToList();

        var distilled = distiller.Distill(turns, new DistillOptions(config.MaxToolResultChars, config.IncludeThinking));
        var chunks = chunker.Chunk(distilled, new ChunkOptions());

        var stored = new List<StoredChunk>(chunks.Count);
        for (var i = 0; i < chunks.Count; i++)
        {
            var c = chunks[i];
            var embedding = await embedder.EmbedAsync(c.Text, ct);
            var id = $"{request.Project}:{request.Session}:{c.TurnIndex}:{i}";
            stored.Add(new StoredChunk(
                id, request.Project, request.Session, c.TurnIndex,
                c.Role, c.Type, c.Timestamp, c.Text, embedding));
        }

        if (stored.Count > 0)
        {
            await store.UpsertAsync(stored, ct);
        }

        var total = await store.CountAsync(request.Session, ct);
        return new StashResult(stored.Count, total, $"{request.Project}:{request.Session}");
    }
}
