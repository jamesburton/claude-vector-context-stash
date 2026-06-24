using CCStash.Core;
using CCStash.Core.Config;
using CCStash.Core.Hooks;

namespace CCStash.Verbs;

/// <summary>Handles the <c>stash</c> verb (invoked by the PreCompact hook). Never throws.</summary>
internal static class StashVerb
{
    /// <summary>Read the hook payload from stdin, stash incrementally, and always exit 0.</summary>
    public static async Task<int> RunAsync(TextReader stdin)
    {
        try
        {
            var input = HookInput.FromJson(await stdin.ReadToEndAsync());
            var cfg = CCStashConfig.Load(CCStashPaths.ConfigPath);
            var svc = Composition.BuildStash(input.Cwd, cfg);
            var result = await svc.StashAsync(
                new StashRequest(input.TranscriptPath, CCStashPaths.ProjectHash(input.Cwd), input.SessionId));
            Log($"stash ok: +{result.NewChunks} ({result.TotalChunks} total) {result.StashId}");
        }
        catch (Exception ex)
        {
            Log($"stash failed: {ex.Message}");
        }

        return 0; // hook safety: always succeed
    }

    private static void Log(string msg)
    {
        try
        {
            Directory.CreateDirectory(CCStashPaths.DataDir);
            File.AppendAllText(CCStashPaths.LogPath, $"{DateTimeOffset.Now:O} {msg}{Environment.NewLine}");
        }
        catch
        {
            // logging must never break the hook
        }
    }
}
