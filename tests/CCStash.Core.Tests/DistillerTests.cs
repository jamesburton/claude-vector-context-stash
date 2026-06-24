using CCStash.Core.Distillation;
using CCStash.Core.Transcript;

namespace CCStash.Core.Tests;

public class DistillerTests
{
    private static TranscriptTurn Turn(int i, string role, params ContentBlock[] blocks) =>
        new(i, role, DateTimeOffset.UnixEpoch, blocks);

    [Fact]
    public void Truncates_tool_results_and_keeps_tool_name()
    {
        var big = new string('x', 5000);
        var turns = new[]
        {
            Turn(0, "assistant",
                new ContentBlock(BlockKind.ToolUse, "{\"path\":\"a.cs\"}", "Read"),
                new ContentBlock(BlockKind.ToolResult, big, null)),
        };

        var d = new Distiller().Distill(turns, new DistillOptions(MaxToolResultChars: 100));

        Assert.Single(d);
        Assert.Contains("Read", d[0].Text);
        Assert.True(d[0].Text.Length < 500);
        Assert.Contains("truncated", d[0].Text, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Excludes_thinking_when_disabled_and_drops_empty_turns()
    {
        var turns = new[]
        {
            Turn(0, "assistant", new ContentBlock(BlockKind.Thinking, "secret reasoning", null)),
            Turn(1, "user", new ContentBlock(BlockKind.Text, "hello", null)),
        };

        var d = new Distiller().Distill(turns, new DistillOptions(IncludeThinking: false));

        Assert.Single(d);
        Assert.Equal("hello", d[0].Text);
        Assert.Equal(1, d[0].Index);
    }
}
