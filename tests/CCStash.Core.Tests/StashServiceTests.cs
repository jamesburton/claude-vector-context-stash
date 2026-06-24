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

    private static StashService Build(IVectorStore store) => new(
        new TranscriptParser(), new Distiller(), new Chunker(),
        new FakeEmbedder(8), store, new CCStashConfig());

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
}
