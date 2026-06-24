namespace CCStash.Core.Transcript;

/// <summary>The kind of a content block within a turn.</summary>
public enum BlockKind
{
    /// <summary>Plain assistant/user text.</summary>
    Text,

    /// <summary>Assistant reasoning.</summary>
    Thinking,

    /// <summary>A tool invocation.</summary>
    ToolUse,

    /// <summary>A tool result payload.</summary>
    ToolResult,
}

/// <summary>One typed piece of a turn's content.</summary>
public sealed record ContentBlock(BlockKind Kind, string Text, string? ToolName);

/// <summary>A single conversation turn with its content blocks.</summary>
public sealed record TranscriptTurn(int Index, string Role, DateTimeOffset? Timestamp, IReadOnlyList<ContentBlock> Blocks);
