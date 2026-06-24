using CCStash.Core;
using CCStash.Core.Embeddings;
using CCStash.Core.Storage;

namespace CCStash.Core.Tests;

public class RetrievalServiceTests
{
    [Fact]
    public async Task Retrieves_most_similar_chunk_text()
    {
        var embedder = new FakeEmbedder(8);
        using var store = new InMemoryVectorStore();
        await store.InitializeAsync(8, embedder.ModelId);
        await store.UpsertAsync([
            new StoredChunk("1", "p", "s1", 0, "user", "turn", null, "database migration plan",
                await embedder.EmbedAsync("database migration plan")),
            new StoredChunk("2", "p", "s1", 1, "user", "turn", null, "lunch menu options",
                await embedder.EmbedAsync("lunch menu options")),
        ]);

        var hits = await new RetrievalService(embedder, store).RetrieveAsync("database migration plan", 1, "s1");

        Assert.Single(hits);
        Assert.Equal("database migration plan", hits[0].Text);
    }
}
