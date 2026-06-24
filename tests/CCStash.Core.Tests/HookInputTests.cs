using CCStash.Core.Hooks;

namespace CCStash.Core.Tests;

public class HookInputTests
{
    [Fact]
    public void Parses_precompact_payload()
    {
        var json = """
        {"session_id":"abc","transcript_path":"/t.jsonl","cwd":"/proj",
         "hook_event_name":"PreCompact","compaction_triggered_by":"auto"}
        """;

        var input = HookInput.FromJson(json);

        Assert.Equal("abc", input.SessionId);
        Assert.Equal("/t.jsonl", input.TranscriptPath);
        Assert.Equal("/proj", input.Cwd);
        Assert.Equal("auto", input.CompactionTriggeredBy);
    }

    [Fact]
    public void Parses_sessionstart_compact_payload()
    {
        var json = """{"session_id":"abc","transcript_path":"/t.jsonl","cwd":"/proj","source":"compact"}""";
        var input = HookInput.FromJson(json);
        Assert.Equal("compact", input.Source);
    }
}
