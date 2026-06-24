namespace CCStash.Core.Storage;

/// <summary>A distilled, embedded unit of conversation stored for later retrieval.</summary>
public sealed record StoredChunk(
    string Id,
    string Project,
    string Session,
    int TurnIndex,
    string Role,
    string Type,
    DateTimeOffset? Timestamp,
    string Text,
    float[] Embedding);

/// <summary>A search result: a stored chunk plus its similarity score (higher is closer).</summary>
public sealed record SearchHit(StoredChunk Chunk, float Score);
