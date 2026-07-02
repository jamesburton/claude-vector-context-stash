using CCStash.Core;
using CCStash.Core.Config;
using CCStash.Core.Hooks;

namespace CCStash.Verbs;

/// <summary>Handles the <c>stash</c> verb (invoked by the PreCompact hook). Never throws.</summary>
internal static class StashVerb
{
    /// <summary>
    /// Read the hook payload from stdin, apply any <c>--transcript</c>/<c>--project</c> overrides,
    /// stash incrementally, and always exit 0.
    /// </summary>
    public static async Task<int> RunAsync(string[] args, TextReader stdin)
    {
        try
        {
            var stdinText = await stdin.ReadToEndAsync();
            var input = BuildInput(args, stdinText);
            var cfg = CCStashConfig.Load(CCStashPaths.ConfigPath);

            // Bound the work so a slow embed can never hang compaction.
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(cfg.StashTimeoutSeconds));
            var svc = await Composition.BuildStashAsync(input.Cwd, cfg);
            var result = await svc.StashAsync(
                new StashRequest(input.TranscriptPath, CCStashPaths.ProjectHash(input.Cwd), input.SessionId),
                cts.Token);
            Log($"stash ok: +{result.NewChunks} ({result.TotalChunks} total) {result.StashId}");

            // Record this project's root against its db hash so `gc` can later distinguish a live
            // project from one whose directory was removed. Best-effort; never throws.
            ProjectRegistry.Record(CCStashPaths.DataDir, input.Cwd, DateTimeOffset.Now.ToString("O"));
        }
        catch (Exception ex)
        {
            Log($"stash failed: {ex.Message}");
        }

        return 0; // hook safety: always succeed
    }

    /// <summary>
    /// Parse stdin as <see cref="HookInput"/> when present, falling back to a minimal input built
    /// from <c>--transcript</c>/<c>--project</c> when stdin is empty/invalid — the CodeSharp hook
    /// invocation supplies no stdin payload, only these CLI args. CLI args always override the
    /// corresponding stdin field when both are present.
    /// </summary>
    private static HookInput BuildInput(string[] args, string stdinText)
    {
        var transcript = ArgValue(args, "--transcript");
        var project = ArgValue(args, "--project");

        HookInput input;
        try
        {
            input = HookInput.FromJson(stdinText);
        }
        catch (System.Text.Json.JsonException) when (transcript is not null && project is not null)
        {
            input = new HookInput(SessionId: Guid.NewGuid().ToString("N"), TranscriptPath: transcript, Cwd: project, Source: null, CompactionTriggeredBy: null);
        }

        if (transcript is not null)
        {
            input = input with { TranscriptPath = transcript };
        }

        if (project is not null)
        {
            input = input with { Cwd = project };
        }

        return input;
    }

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
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
