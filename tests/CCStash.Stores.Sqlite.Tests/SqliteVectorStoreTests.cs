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

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
