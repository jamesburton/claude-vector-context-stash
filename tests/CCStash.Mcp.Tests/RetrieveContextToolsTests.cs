using CCStash.Core;
using CCStash.Core.Embeddings;
using CCStash.Core.Storage;
using CCStash.Mcp;

namespace CCStash.Mcp.Tests;

public class RetrieveContextToolsTests
{
    private static async Task<(IRetrievalService Retrieval, IVectorStore Store)> BuildAsync()
    {
        var embedder = new FakeEmbedder(8);
        var store = new InMemoryVectorStore();
        await store.InitializeAsync(8, embedder.ModelId);
        await store.UpsertAsync([
            new StoredChunk("1", "p", "s1", 0, "user", "turn", DateTimeOffset.UnixEpoch, "database migration plan",
                await embedder.EmbedAsync("database migration plan")),
            new StoredChunk("2", "p", "s1", 1, "user", "turn", DateTimeOffset.UnixEpoch, "lunch menu options",
                await embedder.EmbedAsync("lunch menu options")),
        ]);
        return (new RetrievalService(embedder, store), store);
    }

    [Fact]
    public async Task RetrieveContext_returns_formatted_relevant_chunk()
    {
        var (retrieval, store) = await BuildAsync();

        var result = await RetrieveContextTools.RetrieveContext(
            retrieval, store, new McpToolContext(ProjectWide: true), "database migration plan", limit: 1);

        Assert.Contains("database migration plan", result);
        Assert.Contains("turn 0", result);
    }

    [Fact]
    public async Task RetrieveContext_reports_no_match_gracefully()
    {
        var (retrieval, store) = await BuildAsync();
        using var empty = new InMemoryVectorStore();
        await empty.InitializeAsync(8, "fake-8");
        var emptyRetrieval = new RetrievalService(new FakeEmbedder(8), empty);

        var result = await RetrieveContextTools.RetrieveContext(
            emptyRetrieval, empty, new McpToolContext(ProjectWide: true), "anything", limit: 3);

        Assert.Contains("No stashed context", result);
    }

    [Fact]
    public async Task ListStashes_reports_count_and_latest_session()
    {
        var (_, store) = await BuildAsync();

        var result = await RetrieveContextTools.ListStashes(store);

        Assert.Contains("2 chunks stashed", result);
        Assert.Contains("s1", result);
    }
}
