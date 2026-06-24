using System.Text.Json;

namespace CCStash.Core.Config;

/// <summary>A project root recorded against its db hash, so gc can tell live projects from orphans.</summary>
public sealed record ProjectEntry(string Path, string LastStash);

/// <summary>
/// Maps each project-db hash to the originating project directory (<c>~/.claude/ccstash/projects.json</c>).
/// Db files are named by a one-way <see cref="CCStashPaths.ProjectHash(string)"/>, so the path cannot be
/// recovered from the filename; this manifest is the only record that lets <c>gc</c> distinguish a live
/// project from one whose directory has been deleted.
/// </summary>
public static class ProjectRegistry
{
    /// <summary>Manifest file name within the data directory.</summary>
    public const string FileName = "projects.json";

    private static readonly JsonSerializerOptions Options = new() { WriteIndented = true };

    /// <summary>Full path to the manifest within <paramref name="dataDir"/>.</summary>
    public static string PathFor(string dataDir) => Path.Combine(dataDir, FileName);

    /// <summary>Load the manifest, or an empty map if it is missing or unreadable.</summary>
    public static IReadOnlyDictionary<string, ProjectEntry> Load(string dataDir)
    {
        var path = PathFor(dataDir);
        if (!File.Exists(path))
        {
            return new Dictionary<string, ProjectEntry>();
        }

        try
        {
            return JsonSerializer.Deserialize<Dictionary<string, ProjectEntry>>(File.ReadAllText(path))
                   ?? new Dictionary<string, ProjectEntry>();
        }
        catch (JsonException)
        {
            return new Dictionary<string, ProjectEntry>();
        }
    }

    /// <summary>Persist the manifest, creating the data directory if needed.</summary>
    public static void Save(string dataDir, IReadOnlyDictionary<string, ProjectEntry> entries)
    {
        Directory.CreateDirectory(dataDir);
        File.WriteAllText(PathFor(dataDir), JsonSerializer.Serialize(entries, Options));
    }

    /// <summary>
    /// Upsert the project root for its db hash. Best-effort: any failure is swallowed so recording can
    /// never break the stash hook that calls it.
    /// </summary>
    public static void Record(string dataDir, string cwd, string nowIso)
    {
        try
        {
            var entries = new Dictionary<string, ProjectEntry>(Load(dataDir).ToDictionary(kv => kv.Key, kv => kv.Value))
            {
                [CCStashPaths.ProjectHash(cwd)] = new ProjectEntry(cwd, nowIso),
            };
            Save(dataDir, entries);
        }
        catch
        {
            // recording must never break the hook
        }
    }
}
