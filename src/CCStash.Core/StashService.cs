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
    // Chunks embedded and committed per batch. Batches are committed independently so a timeout
    // (cancellation) keeps the work already done and advances the high-water mark, letting the
    // next compaction resume rather than re-embed the whole transcript from scratch.
    private const int BatchSize = 64;

    /// <inheritdoc/>
    public async Task<StashResult> StashAsync(StashRequest request, CancellationToken ct = default)
    {
        await store.InitializeAsync(embedder.Dimension, embedder.ModelId, allowReset: true, ct);

        var highWater = await store.GetHighWaterMarkAsync(request.Session, ct);
        var turns = parser.Parse(request.TranscriptPath).Where(t => t.Index > highWater).ToList();

        var distilled = distiller.Distill(turns, new DistillOptions(config.MaxToolResultChars, config.IncludeThinking));
        var chunks = chunker.Chunk(distilled, new ChunkOptions());

        var newChunks = 0;
        var ordinal = 0;
        try
        {
            foreach (var batch in BuildTurnAlignedBatches(chunks, BatchSize))
            {
                ct.ThrowIfCancellationRequested();

                var embeddings = await embedder.EmbedBatchAsync(batch.Select(c => c.Text).ToList(), ct);
                var stored = new List<StoredChunk>(batch.Count);
                for (var j = 0; j < batch.Count; j++)
                {
                    var c = batch[j];
                    var id = $"{request.Project}:{request.Session}:{c.TurnIndex}:{ordinal++}";
                    stored.Add(new StoredChunk(
                        id, request.Project, request.Session, c.TurnIndex,
                        c.Role, c.Type, c.Timestamp, c.Text, embeddings[j]));
                }

                // Commit the batch with a non-cancellable token: once embedded, the work is paid
                // for, so let the write finish and persist rather than discarding it on timeout.
                await store.UpsertAsync(stored, CancellationToken.None);
                newChunks += stored.Count;
            }
        }
        catch (OperationCanceledException)
        {
            // Budget exhausted: keep the batches already committed; the next run continues from the
            // advanced high-water mark.
        }

        var total = await store.CountAsync(request.Session, CancellationToken.None);
        return new StashResult(newChunks, total, $"{request.Project}:{request.Session}");
    }

    // Pack chunks into batches of at least <paramref name="target"/>, but only ever close a batch on
    // a turn boundary. A turn's chunks (a turn plus any oversized split parts) therefore commit
    // together, so the per-session high-water mark never lands mid-turn and the next run cannot skip
    // an uncommitted part of an already-counted turn.
    private static IEnumerable<IReadOnlyList<Chunk>> BuildTurnAlignedBatches(IReadOnlyList<Chunk> chunks, int target)
    {
        var batch = new List<Chunk>();
        for (var i = 0; i < chunks.Count; i++)
        {
            batch.Add(chunks[i]);
            var atTurnBoundary = i + 1 >= chunks.Count || chunks[i + 1].TurnIndex != chunks[i].TurnIndex;
            if (batch.Count >= target && atTurnBoundary)
            {
                yield return batch;
                batch = [];
            }
        }

        if (batch.Count > 0)
        {
            yield return batch;
        }
    }
}
