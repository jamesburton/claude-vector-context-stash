using CCStash.Core.Storage;
using CCStash.Stores.Sqlite;

namespace CCStash.Stores.Sqlite.Tests;

public class SqliteVectorStoreTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ccstash-{Guid.NewGuid():N}.db");

    private static StoredChunk Chunk(string id, int turn, string session, float[] v) =>
        new(id, "proj", session, turn, "user", "text", DateTimeOffset.UnixEpoch.AddSeconds(turn), $"text {id}", v);

    [Fact]
    public async Task Roundtrip_search_highwater_and_persistence()
    {
        using (var store = new SqliteVectorStore(_dbPath))
        {
            await store.InitializeAsync(2, "fake");
            await store.UpsertAsync([
                Chunk("a", 0, "s1", [1f, 0f]),
                Chunk("b", 5, "s1", [0f, 1f]),
            ]);

            var hits = await store.SearchAsync([1f, 0f], limit: 1, session: "s1");
            Assert.Equal("a", hits[0].Chunk.Id);
            Assert.Equal(5, await store.GetHighWaterMarkAsync("s1"));
            Assert.Equal("s1", await store.GetLatestSessionAsync());
        }

        // Reopen: data persists to the single file.
        using var reopened = new SqliteVectorStore(_dbPath);
        await reopened.InitializeAsync(2, "fake");
        Assert.Equal(2, await reopened.CountAsync("s1"));
    }

    [Fact]
    public async Task Changing_embedding_model_wipes_incompatible_vectors()
    {
        using (var store = new SqliteVectorStore(_dbPath))
        {
            await store.InitializeAsync(2, "model-a");
            await store.UpsertAsync([Chunk("a", 0, "s1", [1f, 0f])]);
            Assert.Equal(1, await store.CountAsync("s1"));
        }

        // Reopen with a different model: prior vectors are not comparable, so they are cleared.
        using (var store = new SqliteVectorStore(_dbPath))
        {
            await store.InitializeAsync(3, "model-b");
            Assert.Equal(0, await store.CountAsync("s1"));
        }

        // Reopen with the same model: data is preserved.
        using (var store = new SqliteVectorStore(_dbPath))
        {
            await store.InitializeAsync(3, "model-b");
            await store.UpsertAsync([Chunk("c", 0, "s1", [1f, 0f, 0f])]);
        }

        using var reopened = new SqliteVectorStore(_dbPath);
        await reopened.InitializeAsync(3, "model-b");
        Assert.Equal(1, await reopened.CountAsync("s1"));
    }

    [Fact]
    public async Task Hybrid_search_surfaces_keyword_match_a_pure_vector_query_would_miss()
    {
        using var store = new SqliteVectorStore(_dbPath);
        await store.InitializeAsync(3, "hybrid-model");
        await store.UpsertAsync([
            // A: vector points away from the query, but contains the rare query keyword.
            new StoredChunk("A", "p", "s1", 0, "user", "turn", DateTimeOffset.UnixEpoch, "the quarterly xyzzy compliance report", [1f, 0f, 0f]),
            // B: vector matches the query exactly, but is topically unrelated.
            new StoredChunk("B", "p", "s1", 1, "user", "turn", DateTimeOffset.UnixEpoch, "a recipe for chocolate cake", [0f, 1f, 0f]),
        ]);

        // Pure-vector (no query text) ranks B first.
        var vectorOnly = await store.SearchAsync([0f, 1f, 0f], limit: 1, session: "s1");
        Assert.Equal("B", vectorOnly[0].Chunk.Id);

        // Hybrid (query text "xyzzy") fuses keyword relevance and lifts A to the top.
        var hybrid = await store.SearchAsync([0f, 1f, 0f], limit: 1, session: "s1", queryText: "xyzzy");
        Assert.Equal("A", hybrid[0].Chunk.Id);
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
