using CCStash.Core;
using CCStash.Core.Chunking;
using CCStash.Core.Config;
using CCStash.Core.Distillation;
using CCStash.Core.Embeddings;
using CCStash.Core.Storage;
using CCStash.Core.Transcript;
using CCStash.Stores.Sqlite;

namespace CCStash;

/// <summary>Builds CCStash services from configuration. Single composition root.</summary>
internal static class Composition
{
    /// <summary>
    /// Build the embedder. The plan wires <c>OnnxEmbedder</c> here when a local model is present
    /// (under <c>~/.claude/ccstash/models/</c>); until then this falls back to the deterministic
    /// <see cref="FakeEmbedder"/> so the pipeline runs offline with no external dependency.
    /// </summary>
    public static IEmbedder BuildEmbedder(CCStashConfig cfg) => new FakeEmbedder(384);

    /// <summary>Build the project-scoped vector store.</summary>
    public static IVectorStore BuildStore(string cwd, CCStashConfig cfg)
        => new SqliteVectorStore(CCStashPaths.DbPath(cwd));

    /// <summary>Build the stash service for a project.</summary>
    public static IStashService BuildStash(string cwd, CCStashConfig cfg)
        => new StashService(new TranscriptParser(), new Distiller(), new Chunker(),
            BuildEmbedder(cfg), BuildStore(cwd, cfg), cfg);

    /// <summary>Build the retrieval service for a project.</summary>
    public static IRetrievalService BuildRetrieval(string cwd, CCStashConfig cfg)
        => new RetrievalService(BuildEmbedder(cfg), BuildStore(cwd, cfg));
}
