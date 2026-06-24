using CCStash.Core.Storage;

namespace CCStash.Core.Tests;

public class InMemoryVectorStoreTests
{
    private static StoredChunk Chunk(string id, int turn, string session, float[] v) =>
        new(id, "proj", session, turn, "user", "text", DateTimeOffset.UnixEpoch, $"text {id}", v);

    [Fact]
    public async Task Search_ranks_by_cosine_similarity()
    {
        using var store = new InMemoryVectorStore();
        await store.InitializeAsync(2, "fake");
        await store.UpsertAsync([
            Chunk("a", 0, "s1", [1f, 0f]),
            Chunk("b", 1, "s1", [0f, 1f]),
        ]);

        var hits = await store.SearchAsync([1f, 0f], limit: 1, session: null);

        Assert.Single(hits);
        Assert.Equal("a", hits[0].Chunk.Id);
    }

    [Fact]
    public async Task Search_filters_by_session_when_provided()
    {
        using var store = new InMemoryVectorStore();
        await store.InitializeAsync(2, "fake");
        await store.UpsertAsync([
            Chunk("a", 0, "s1", [1f, 0f]),
            Chunk("b", 0, "s2", [1f, 0f]),
        ]);

        var hits = await store.SearchAsync([1f, 0f], limit: 5, session: "s2");

        Assert.Single(hits);
        Assert.Equal("s2", hits[0].Chunk.Session);
    }

    [Fact]
    public async Task Upsert_replaces_by_id_and_highwater_tracks_max_turn()
    {
        using var store = new InMemoryVectorStore();
        await store.InitializeAsync(2, "fake");
        await store.UpsertAsync([Chunk("a", 3, "s1", [1f, 0f])]);
        await store.UpsertAsync([Chunk("a", 3, "s1", [0f, 1f])]); // same id

        Assert.Equal(1, await store.CountAsync("s1"));
        Assert.Equal(3, await store.GetHighWaterMarkAsync("s1"));
        Assert.Equal(-1, await store.GetHighWaterMarkAsync("missing"));
    }
}
