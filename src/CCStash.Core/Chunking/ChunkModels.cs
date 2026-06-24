namespace CCStash.Core.Chunking;

/// <summary>Controls chunk sizing.</summary>
public sealed record ChunkOptions(int MaxChars = 3200);

/// <summary>An embeddable chunk derived from a distilled turn.</summary>
public sealed record Chunk(int TurnIndex, string Role, DateTimeOffset? Timestamp, string Type, string Text);
