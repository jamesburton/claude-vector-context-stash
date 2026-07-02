using System.Text.Json;
using System.Text.Json.Nodes;

namespace CCStash.Core.Install;

/// <summary>
/// Shared primitives for reading, idempotently merging, and writing JSON config files used by
/// <see cref="IAgentAdapter"/> implementations. Extracted from the logic that used to live inline in
/// the old <c>init</c> verb.
/// </summary>
public static class JsonConfigEditor
{
    private static readonly JsonSerializerOptions WriteOptions = new() { WriteIndented = true };

    /// <summary>Load <paramref name="path"/> as a JSON object, or an empty object if it is missing or unreadable.</summary>
    public static JsonObject LoadOrEmpty(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    /// <summary>Serialize <paramref name="root"/> to <paramref name="path"/>, creating parent directories as needed.</summary>
    public static void Save(string path, JsonObject root)
    {
        var full = Path.GetFullPath(path);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, root.ToJsonString(WriteOptions));
    }

    /// <summary>Get the object at <paramref name="key"/> under <paramref name="container"/>, creating it if absent.</summary>
    public static JsonObject GetOrCreateObject(JsonObject container, string key)
    {
        var obj = container[key] as JsonObject ?? new JsonObject();
        container[key] = obj;
        return obj;
    }

    /// <summary>
    /// Ensure the array at <paramref name="arrayKey"/> under <paramref name="container"/> contains an
    /// entry satisfying <paramref name="isMatch"/>; appends <paramref name="buildEntry"/>'s result if
    /// none does.
    /// </summary>
    /// <returns><see langword="true"/> if a matching entry already existed (no-op); otherwise <see langword="false"/>.</returns>
    public static bool EnsureArrayEntry(
        JsonObject container,
        string arrayKey,
        Func<JsonObject, bool> isMatch,
        Func<JsonObject> buildEntry)
    {
        var arr = container[arrayKey] as JsonArray ?? new JsonArray();
        container[arrayKey] = arr;

        if (arr.OfType<JsonObject>().Any(isMatch))
        {
            return true;
        }

        arr.Add(buildEntry());
        return false;
    }

    /// <summary>Ensure <paramref name="container"/>[<paramref name="key"/>] deep-equals <paramref name="value"/>, setting it if absent or different.</summary>
    /// <returns><see langword="true"/> if it already equaled <paramref name="value"/> (no-op); otherwise <see langword="false"/>.</returns>
    public static bool EnsureChild(JsonObject container, string key, JsonNode value)
    {
        var existing = container[key];
        if (existing is not null && JsonNode.DeepEquals(existing, value))
        {
            return true;
        }

        container[key] = value;
        return false;
    }

    /// <summary>Remove all entries in the array at <paramref name="arrayKey"/> satisfying <paramref name="isMatch"/>.</summary>
    /// <returns>The number of entries removed.</returns>
    public static int RemoveArrayEntries(JsonObject container, string arrayKey, Func<JsonObject, bool> isMatch)
    {
        if (container[arrayKey] is not JsonArray arr)
        {
            return 0;
        }

        var toRemove = arr.OfType<JsonObject>().Where(isMatch).ToList();
        foreach (var entry in toRemove)
        {
            arr.Remove(entry);
        }

        return toRemove.Count;
    }

    /// <summary>Remove <paramref name="key"/> from <paramref name="container"/> if present.</summary>
    /// <returns><see langword="true"/> if it was present and removed; otherwise <see langword="false"/>.</returns>
    public static bool RemoveChild(JsonObject container, string key) => container.Remove(key);
}
