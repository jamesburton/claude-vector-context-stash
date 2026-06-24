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

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
