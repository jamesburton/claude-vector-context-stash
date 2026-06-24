using System.Text.Json;

namespace CCStash.Core.Config;

/// <summary>User-tunable CCStash settings.</summary>
public sealed record CCStashConfig(
    string Store = "sqlite",
    string EmbeddingProvider = "onnx",
    string EmbeddingModel = "all-MiniLM-L6-v2",
    int MaxToolResultChars = 800,
    bool IncludeThinking = true,
    string RetrievalScope = "session",
    int RetrievalLimit = 6)
{
    /// <summary>True when retrieval should span all sessions in the project.</summary>
    public bool ProjectWide => string.Equals(RetrievalScope, "project", StringComparison.OrdinalIgnoreCase);

    /// <summary>Load config from <paramref name="path"/>, or defaults if it is missing/invalid.</summary>
    public static CCStashConfig Load(string path)
    {
        if (!File.Exists(path))
        {
            return new CCStashConfig();
        }

        try
        {
            return JsonSerializer.Deserialize<CCStashConfig>(File.ReadAllText(path)) ?? new CCStashConfig();
        }
        catch (JsonException)
        {
            return new CCStashConfig();
        }
    }
}
