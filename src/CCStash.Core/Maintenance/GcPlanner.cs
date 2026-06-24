using CCStash.Core.Config;

namespace CCStash.Core.Maintenance;

/// <summary>How <c>gc</c> classified a project db.</summary>
public enum GcDisposition
{
    /// <summary>The recorded project directory still exists; the db is kept.</summary>
    Live,

    /// <summary>The recorded project directory is gone; the db is an orphan and is removed.</summary>
    Orphan,

    /// <summary>No manifest entry attributes this db to a project; kept unless prune-unknown is set.</summary>
    Unknown,
}

/// <summary>A single db considered by <c>gc</c>, with its classification and whether it will be removed.</summary>
public sealed record GcItem(string Hash, string? Path, GcDisposition Disposition, bool Removed);

/// <summary>
/// Pure planning for <c>gc</c>: decides, per db hash, whether it is a live project, an orphan, or of
/// unknown origin. The safety rule is structural — a db is only ever removed when its recorded project
/// directory no longer exists, or (opt-in) when it has no manifest entry at all. A db whose project
/// still exists is never removed, so cleanup can never delete another live project's stash.
/// </summary>
public static class GcPlanner
{
    /// <summary>
    /// Classify each db hash against the manifest. <paramref name="pathExists"/> is injected so the
    /// decision is testable without touching the filesystem.
    /// </summary>
    public static IReadOnlyList<GcItem> Plan(
        IEnumerable<string> dbHashes,
        IReadOnlyDictionary<string, ProjectEntry> manifest,
        Func<string, bool> pathExists,
        bool pruneUnknown)
    {
        var items = new List<GcItem>();
        foreach (var hash in dbHashes)
        {
            if (manifest.TryGetValue(hash, out var entry))
            {
                var live = pathExists(entry.Path);
                items.Add(new GcItem(
                    hash,
                    entry.Path,
                    live ? GcDisposition.Live : GcDisposition.Orphan,
                    Removed: !live));
            }
            else
            {
                items.Add(new GcItem(hash, null, GcDisposition.Unknown, Removed: pruneUnknown));
            }
        }

        return items;
    }
}
