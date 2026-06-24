using System.Net.Sockets;
using CCStash.Core.Storage;
using CCStash.Stores.Qdrant;

namespace CCStash.Stores.Qdrant.Tests;

public class QdrantVectorStoreTests
{
    // Integration tests against a local Qdrant (gRPC 6334). When none is reachable they no-op,
    // since xUnit 2.9 has no dynamic skip. CI/dev provides one via `docker run qdrant/qdrant`.
    private const string Host = "localhost";
    private const int Port = 6334;

    private static bool QdrantReachable()
    {
        try
        {
            using var c = new TcpClient();
            return c.ConnectAsync(Host, Port).Wait(TimeSpan.FromSeconds(1)) && c.Connected;
        }
        catch
        {
            return false;
        }
    }

    private static StoredChunk Chunk(string id, int turn, string session, float[] v, string text) =>
        new(id, "projtest", session, turn, "user", "turn", DateTimeOffset.UnixEpoch.AddSeconds(turn), text, v);

    [Fact]
    public async Task Roundtrip_search_count_highwater_latest()
    {
        if (!QdrantReachable())
        {
            return;
        }

        // Unique base name per run so reruns don't collide.
        var baseName = "t" + Guid.NewGuid().ToString("N")[..8];
        using var store = new QdrantVectorStore(Host, Port, baseName);
        await store.InitializeAsync(3, "itest-model");

        await store.UpsertAsync([
            Chunk("projtest:s1:0:0", 0, "s1", [1f, 0f, 0f], "alpha vector one"),
            Chunk("projtest:s1:7:0", 7, "s1", [0f, 1f, 0f], "beta vector two"),
            Chunk("projtest:s2:9:0", 9, "s2", [0f, 0f, 1f], "gamma other session"), // newest timestamp
        ]);

        Assert.Equal(3, await store.CountAsync(null));
        Assert.Equal(2, await store.CountAsync("s1"));
        Assert.Equal(7, await store.GetHighWaterMarkAsync("s1"));
        Assert.Equal(-1, await store.GetHighWaterMarkAsync("missing"));

        var hits = await store.SearchAsync([1f, 0f, 0f], limit: 1, session: "s1");
        Assert.Single(hits);
        Assert.Equal("projtest:s1:0:0", hits[0].Chunk.Id);
        Assert.Equal("alpha vector one", hits[0].Chunk.Text);

        Assert.Equal("s2", await store.GetLatestSessionAsync()); // s2 has the latest timestamp
    }

    [Fact]
    public async Task Upsert_replaces_by_id()
    {
        if (!QdrantReachable())
        {
            return;
        }

        var baseName = "t" + Guid.NewGuid().ToString("N")[..8];
        using var store = new QdrantVectorStore(Host, Port, baseName);
        await store.InitializeAsync(3, "itest-model");

        await store.UpsertAsync([Chunk("projtest:s1:0:0", 0, "s1", [1f, 0f, 0f], "original")]);
        await store.UpsertAsync([Chunk("projtest:s1:0:0", 0, "s1", [1f, 0f, 0f], "updated")]);

        Assert.Equal(1, await store.CountAsync("s1"));
        var hits = await store.SearchAsync([1f, 0f, 0f], 1, "s1");
        Assert.Equal("updated", hits[0].Chunk.Text);
    }
}
