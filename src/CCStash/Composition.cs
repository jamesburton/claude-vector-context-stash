using CCStash.Core;
using CCStash.Core.Chunking;
using CCStash.Core.Config;
using CCStash.Core.Distillation;
using CCStash.Core.Embeddings;
using CCStash.Core.Storage;
using CCStash.Core.Transcript;
using CCStash.Embeddings.Onnx;
using CCStash.Stores.Qdrant;
using CCStash.Stores.Sqlite;

namespace CCStash;

/// <summary>Builds CCStash services from configuration. Single composition root.</summary>
internal static class Composition
{
    /// <summary>
    /// Build the embedder. Uses the local ONNX model when present under the configured model
    /// directory; otherwise falls back to the deterministic <see cref="FakeEmbedder"/> so the
    /// pipeline still runs offline with no external dependency (hook safety).
    /// </summary>
    public static async Task<IEmbedder> BuildEmbedderAsync(CCStashConfig cfg)
    {
        if (cfg.EmbeddingProvider == "onnx")
        {
            var dir = Environment.GetEnvironmentVariable("CCSTASH_MODEL_DIR")
                      ?? Path.Combine(CCStashPaths.DataDir, "models", cfg.EmbeddingModel);
            if (File.Exists(Path.Combine(dir, "model.onnx")) && File.Exists(Path.Combine(dir, "tokenizer.json")))
            {
                return await OnnxEmbedder.LoadAsync(dir);
            }
        }

        return new FakeEmbedder(384);
    }

    /// <summary>Build the project-scoped vector store selected by config (<c>sqlite</c> or <c>qdrant</c>).</summary>
    public static IVectorStore BuildStore(string cwd, CCStashConfig cfg)
        => cfg.Store.Equals("qdrant", StringComparison.OrdinalIgnoreCase)
            ? new QdrantVectorStore(cfg.QdrantHost, cfg.QdrantPort, CCStashPaths.ProjectHash(cwd), cfg.QdrantApiKey)
            : new SqliteVectorStore(CCStashPaths.DbPath(cwd));

    /// <summary>Build the stash service for a project.</summary>
    public static async Task<IStashService> BuildStashAsync(string cwd, CCStashConfig cfg)
        => new StashService(new TranscriptParser(), new Distiller(), new Chunker(),
            await BuildEmbedderAsync(cfg), BuildStore(cwd, cfg), cfg);

    /// <summary>Build the retrieval service for a project.</summary>
    public static async Task<IRetrievalService> BuildRetrievalAsync(string cwd, CCStashConfig cfg)
        => new RetrievalService(await BuildEmbedderAsync(cfg), BuildStore(cwd, cfg));
}
