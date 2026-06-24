using CCStash.Core.Config;

namespace CCStash.Verbs;

/// <summary>Handles the <c>status</c> verb: prints stash stats for the current project.</summary>
internal static class StatusVerb
{
    /// <summary>Print DB path, model, chunk count, and latest session.</summary>
    public static async Task<int> RunAsync(string cwd, TextWriter stdout)
    {
        var cfg = CCStashConfig.Load(CCStashPaths.ConfigPath);
        using var store = Composition.BuildStore(cwd, cfg);
        var embedder = Composition.BuildEmbedder(cfg);
        await store.InitializeAsync(embedder.Dimension, embedder.ModelId);

        var total = await store.CountAsync(null);
        var latest = await store.GetLatestSessionAsync();
        await stdout.WriteLineAsync($"DB: {CCStashPaths.DbPath(cwd)}");
        await stdout.WriteLineAsync($"Model: {embedder.ModelId} (dim {embedder.Dimension})");
        await stdout.WriteLineAsync($"Chunks: {total}; latest session: {latest ?? "(none)"}");
        return 0;
    }
}
