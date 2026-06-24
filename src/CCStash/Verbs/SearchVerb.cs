using CCStash.Core.Config;

namespace CCStash.Verbs;

/// <summary>Handles the <c>search</c> verb: ad-hoc semantic search for debugging.</summary>
internal static class SearchVerb
{
    /// <summary>Search the stash and print scored previews.</summary>
    public static async Task<int> RunAsync(string cwd, string query, TextWriter stdout)
    {
        var cfg = CCStashConfig.Load(CCStashPaths.ConfigPath);
        var svc = Composition.BuildRetrieval(cwd, cfg);
        var hits = await svc.RetrieveAsync(query, cfg.RetrievalLimit, session: null);
        if (hits.Count == 0)
        {
            await stdout.WriteLineAsync("(no matches)");
            return 0;
        }

        foreach (var h in hits)
        {
            await stdout.WriteLineAsync($"[{h.Score:F3}] turn {h.TurnIndex} ({h.Role}): {Preview(h.Text)}");
        }

        return 0;
    }

    private static string Preview(string text)
    {
        var oneLine = text.ReplaceLineEndings(" ").Trim();
        return oneLine.Length <= 120 ? oneLine : oneLine[..120] + "…";
    }
}
