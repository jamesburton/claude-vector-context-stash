using CCStash.Verbs;

namespace CCStash.Tests;

public class StashVerbArgsTests
{
    [Fact]
    public async Task RunAsync_uses_transcript_arg_when_stdin_has_no_valid_json()
    {
        // Empty stdin would normally make HookInput.FromJson throw; --transcript/--project let the
        // CodeSharp hook (which has no stdin payload) still resolve a usable HookInput.
        var dir = Path.Combine(Path.GetTempPath(), $"ccstash-stash-args-{Guid.NewGuid():N}");
        Directory.CreateDirectory(dir);
        try
        {
            var missingTranscript = Path.Combine(dir, "transcript.jsonl");
            var args = new[] { "--transcript", missingTranscript, "--project", dir };

            var exitCode = await StashVerb.RunAsync(args, new StringReader(string.Empty));

            // stash is hook-safe: even with a nonexistent transcript file, it logs and returns 0.
            Assert.Equal(0, exitCode);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
