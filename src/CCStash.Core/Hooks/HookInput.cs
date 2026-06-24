using System.Text.Json;
using System.Text.Json.Serialization;

namespace CCStash.Core.Hooks;

/// <summary>The JSON Claude Code passes to a hook on stdin (fields we use).</summary>
public sealed record HookInput(
    [property: JsonPropertyName("session_id")] string SessionId,
    [property: JsonPropertyName("transcript_path")] string TranscriptPath,
    [property: JsonPropertyName("cwd")] string Cwd,
    [property: JsonPropertyName("source")] string? Source,
    [property: JsonPropertyName("compaction_triggered_by")] string? CompactionTriggeredBy)
{
    /// <summary>Parse a hook payload; missing optional fields become null.</summary>
    public static HookInput FromJson(string json)
        => JsonSerializer.Deserialize<HookInput>(json)
           ?? throw new JsonException("Empty hook input.");
}
