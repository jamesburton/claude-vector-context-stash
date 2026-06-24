using CCStash.Core;
using CCStash.Core.Chunking;
using CCStash.Core.Config;
using CCStash.Core.Distillation;
using CCStash.Core.Embeddings;
using CCStash.Core.Storage;
using CCStash.Core.Transcript;

namespace CCStash.Core.Tests;

public class StashServiceTests
{
    private static string WriteTranscript(params string[] lines)
    {
        var path = Path.Combine(Path.GetTempPath(), $"t-{Guid.NewGuid():N}.jsonl");
        File.WriteAllLines(path, lines);
        return path;
    }

    private static StashService Build(IVectorStore store) => Build(store, new FakeEmbedder(8));

    private static StashService Build(IVectorStore store, IEmbedder embedder) => new(
        new TranscriptParser(), new Distiller(), new Chunker(),
        embedder, store, new CCStashConfig());

    // Embeds normally until <paramref name="cancelAfter"/> chunks have been embedded, then throws
    // OperationCanceledException at the start of the next batch — simulating the stash timeout
    // firing partway through a large transcript.
    private sealed class CancelAfterEmbedder(int cancelAfter) : IEmbedder
    {
        private readonly FakeEmbedder _inner = new(8);
        private int _embedded;

        public int Dimension => _inner.Dimension;

        public string ModelId => _inner.ModelId;

        public Task<float[]> EmbedAsync(string text, CancellationToken ct = default) => _inner.EmbedAsync(text, ct);

        public async Task<IReadOnlyList<float[]>> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken ct = default)
        {
            if (_embedded >= cancelAfter)
            {
                throw new OperationCanceledException();
            }

            _embedded += texts.Count;
            return await _inner.EmbedBatchAsync(texts, ct);
        }
    }

    [Fact]
    public async Task Stashes_turns_and_is_incremental_on_second_run()
    {
        using var store = new InMemoryVectorStore();
        await store.InitializeAsync(8, "fake-8");

        var path = WriteTranscript(
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"first question\"}}",
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"text\",\"text\":\"first answer\"}]}}");
        var req = new StashRequest(path, "proj", "s1");

        var r1 = await Build(store).StashAsync(req);
        var r2 = await Build(store).StashAsync(req); // same transcript, nothing new

        Assert.Equal(2, r1.NewChunks);
        Assert.Equal(0, r2.NewChunks);
        Assert.Equal(2, r2.TotalChunks);
        Assert.Equal("proj:s1", r2.StashId);

        File.Delete(path);
    }

    [Fact]
    public async Task Cancellation_keeps_committed_batches_and_resumes_on_next_run()
    {
        using var store = new InMemoryVectorStore();
        await store.InitializeAsync(8, "fake-8");

        // 150 single-chunk turns -> three batches of 64/64/22. The embedder cancels at the third
        // batch, so the first 128 must already be committed and the high-water mark advanced.
        var lines = Enumerable.Range(0, 150)
            .Select(i => $"{{\"type\":\"user\",\"message\":{{\"role\":\"user\",\"content\":\"turn {i}\"}}}}")
            .ToArray();
        var path = WriteTranscript(lines);
        var req = new StashRequest(path, "proj", "s1");

        var r1 = await Build(store, new CancelAfterEmbedder(128)).StashAsync(req);

        Assert.Equal(128, r1.NewChunks);
        Assert.Equal(128, r1.TotalChunks);

        // Next run (no cancellation) resumes from the high-water mark and stashes only the rest.
        var r2 = await Build(store).StashAsync(req);

        Assert.Equal(22, r2.NewChunks);
        Assert.Equal(150, r2.TotalChunks);

        File.Delete(path);
    }
}
