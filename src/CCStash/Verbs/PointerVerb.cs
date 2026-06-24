using System.Text.Json;
using CCStash.Core.Config;
using CCStash.Core.Hooks;

namespace CCStash.Verbs;

/// <summary>Handles the <c>pointer</c> verb (SessionStart:compact). Emits a minimal pointer only.</summary>
internal static class PointerVerb
{
    /// <summary>Emit a short additionalContext pointer if a stash exists; otherwise nothing.</summary>
    public static async Task<int> RunAsync(TextReader stdin, TextWriter stdout)
    {
        try
        {
            var input = HookInput.FromJson(await stdin.ReadToEndAsync());
            var cfg = CCStashConfig.Load(CCStashPaths.ConfigPath);
            using var store = Composition.BuildStore(input.Cwd, cfg);

            // Pointer only needs a count, so it avoids loading the embedder. allowReset defaults to
            // false, so this read-only init can never wipe the stash even if the passed identity
            // differs from what stash recorded (count does not depend on dimension/model).
            await store.InitializeAsync(384, cfg.EmbeddingModel);
            var count = await store.CountAsync(input.SessionId);
            if (count == 0)
            {
                return 0; // nothing stashed: emit nothing
            }

            var pointer =
                $"🗄️ Detailed pre-compaction context for this session is stashed " +
                $"({count} chunks). Call the `retrieve_context` tool to pull specific earlier " +
                $"details (decisions, file contents, errors) when you need them.";

            var output = new
            {
                hookSpecificOutput = new
                {
                    hookEventName = "SessionStart",
                    additionalContext = pointer,
                },
            };
            await stdout.WriteAsync(JsonSerializer.Serialize(output));
        }
        catch
        {
            // hook safety: emit nothing on failure
        }

        return 0;
    }
}
