using CCStash.Core;
using CCStash.Core.Chunking;
using CCStash.Core.Config;
using CCStash.Core.Distillation;
using CCStash.Core.Embeddings;
using CCStash.Core.Storage;
using CCStash.Core.Transcript;
using CCStash.Embeddings.Onnx;
using CCStash.Mcp;
using CCStash.Stores.Sqlite;

namespace CCStash.IntegrationTests;

/// <summary>
/// Full-pipeline tests: real transcript fixture → parse → distill → chunk → embed → SQLite store
/// → retrieval and the MCP-facing tool. Uses the local ONNX model when present (semantic
/// assertions); otherwise exercises the wiring with the deterministic fake embedder.
/// </summary>
public class EndToEndTests : IDisposable
{
    private readonly string _dbPath = Path.Combine(Path.GetTempPath(), $"ccstash-e2e-{Guid.NewGuid():N}.db");

    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "sample-transcript.jsonl");

    private static string? ModelDir()
    {
        var dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),
            ".claude", "ccstash", "models", "all-MiniLM-L6-v2");
        return File.Exists(Path.Combine(dir, "model.onnx")) ? dir : null;
    }

    private static async Task<IEmbedder> EmbedderAsync()
    {
        var dir = ModelDir();
        return dir is null ? new FakeEmbedder(384) : await OnnxEmbedder.LoadAsync(dir);
    }

    [Fact]
    public async Task Stash_then_retrieve_returns_relevant_context()
    {
        var embedder = await EmbedderAsync();
        using var store = new SqliteVectorStore(_dbPath);
        var cfg = new CCStashConfig();

        var stash = new StashService(
            new TranscriptParser(), new Distiller(), new Chunker(), embedder, store, cfg);
        var result = await stash.StashAsync(new StashRequest(Fixture, "proj", "sess"));

        Assert.True(result.NewChunks >= 6, $"expected the fixture turns to be stashed, got {result.NewChunks}");

        // Incremental: a second stash of the same transcript adds nothing.
        var again = await stash.StashAsync(new StashRequest(Fixture, "proj", "sess"));
        Assert.Equal(0, again.NewChunks);

        // Retrieval (the MCP-facing path) finds the vector-store discussion.
        var retrieval = new RetrievalService(embedder, store);
        var text = await RetrieveContextTools.RetrieveContext(
            retrieval, store, new McpToolContext(ProjectWide: true),
            "which vector database should we use", limit: 3);

        Assert.Contains("sqlite", text, StringComparison.OrdinalIgnoreCase);

        // With the real model, the top hit should be a store-decision turn, not the pointer turn.
        if (ModelDir() is not null)
        {
            var hits = await retrieval.RetrieveAsync("which vector database should we use", 1, "sess");
            Assert.Contains("store", hits[0].Text, StringComparison.OrdinalIgnoreCase);
        }
    }

    [Fact]
    public async Task Tool_results_are_truncated_not_dumped()
    {
        // A transcript whose tool_result is enormous; the distiller must truncate it.
        var big = new string('Z', 20_000);
        var path = Path.Combine(Path.GetTempPath(), $"big-{Guid.NewGuid():N}.jsonl");
        await File.WriteAllLinesAsync(path, [
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"read the file\"}}",
            "{\"type\":\"assistant\",\"message\":{\"role\":\"assistant\",\"content\":[{\"type\":\"tool_use\",\"name\":\"Read\",\"input\":{\"p\":\"x\"}}]}}",
            "{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":[{\"type\":\"tool_result\",\"content\":\"" + big + "\"}]}}",
        ]);

        try
        {
            var embedder = await EmbedderAsync();
            using var store = new SqliteVectorStore(_dbPath);
            var cfg = new CCStashConfig() with { MaxToolResultChars = 500 };
            var stash = new StashService(
                new TranscriptParser(), new Distiller(), new Chunker(), embedder, store, cfg);

            await stash.StashAsync(new StashRequest(path, "proj", "big"));
            var retrieval = new RetrievalService(embedder, store);
            var hits = await retrieval.RetrieveAsync("file contents", 10, "big");

            Assert.All(hits, h => Assert.DoesNotContain(new string('Z', 1000), h.Text));
            Assert.Contains(hits, h => h.Text.Contains("truncated", StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            File.Delete(path);
        }
    }

    public void Dispose()
    {
        if (File.Exists(_dbPath))
        {
            File.Delete(_dbPath);
        }
    }
}
