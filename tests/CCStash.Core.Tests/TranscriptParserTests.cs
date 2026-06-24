using CCStash.Core.Transcript;

namespace CCStash.Core.Tests;

public class TranscriptParserTests
{
    private static string Fixture => Path.Combine(AppContext.BaseDirectory, "fixtures", "sample-transcript.jsonl");

    [Fact]
    public void Parses_turns_with_typed_blocks_and_sequential_index()
    {
        var turns = new TranscriptParser().Parse(Fixture);

        Assert.NotEmpty(turns);
        Assert.Contains(turns, t => t.Role == "user");
        Assert.Contains(turns, t => t.Blocks.Any(b => b.Kind == BlockKind.Text));
        Assert.Contains(turns, t => t.Blocks.Any(b => b.Kind == BlockKind.ToolUse));
        Assert.Equal(turns.Select((_, i) => i), turns.Select(t => t.Index)); // 0..n-1
    }

    [Fact]
    public void Skips_unparseable_lines_without_throwing()
    {
        var path = Path.Combine(Path.GetTempPath(), $"bad-{Guid.NewGuid():N}.jsonl");
        File.WriteAllText(path, "not json\n{\"type\":\"user\",\"message\":{\"role\":\"user\",\"content\":\"hi\"}}\n");
        try
        {
            var turns = new TranscriptParser().Parse(path);
            Assert.Single(turns);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
