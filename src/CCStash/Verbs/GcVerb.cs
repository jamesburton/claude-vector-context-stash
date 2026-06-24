using CCStash.Core.Config;
using CCStash.Core.Maintenance;

namespace CCStash.Verbs;

/// <summary>
/// Handles the <c>gc</c> verb: removes orphaned per-project stash databases — those whose originating
/// project directory no longer exists. Databases for projects that still exist are never touched, and
/// databases with no manifest entry are kept unless <c>--prune-unknown</c> is given. Supports
/// <c>--dry-run</c> to preview without deleting.
/// </summary>
internal static class GcVerb
{
    private static readonly string[] Sidecars = [".db", ".db-shm", ".db-wal"];

    /// <summary>Plan and (unless dry-run) delete orphaned project databases, printing a report.</summary>
    public static Task<int> RunAsync(string[] args, TextWriter stdout)
    {
        var dryRun = args.Contains("--dry-run");
        var pruneUnknown = args.Contains("--prune-unknown");
        var dataDir = CCStashPaths.DataDir;

        if (!Directory.Exists(dataDir))
        {
            stdout.WriteLine("No CCStash data directory; nothing to collect.");
            return Task.FromResult(0);
        }

        var hashes = Directory.GetFiles(dataDir, "*.db")
            .Select(Path.GetFileNameWithoutExtension)
            .Where(h => !string.IsNullOrEmpty(h))
            .Select(h => h!)
            .ToList();

        var manifest = ProjectRegistry.Load(dataDir);
        var plan = GcPlanner.Plan(hashes, manifest, Directory.Exists, pruneUnknown);
        var surviving = manifest.ToDictionary(kv => kv.Key, kv => kv.Value);

        int removed = 0, live = 0, unknownKept = 0, failed = 0;
        long freed = 0;

        foreach (var item in plan)
        {
            switch (item.Disposition)
            {
                case GcDisposition.Live:
                    live++;
                    stdout.WriteLine($"  keep         {item.Hash}  {item.Path}");
                    break;

                case GcDisposition.Orphan:
                    freed += SizeOf(dataDir, item.Hash);
                    if (TryRemove(dataDir, item.Hash, dryRun, out var orphanErr))
                    {
                        removed++;
                        surviving.Remove(item.Hash);
                        stdout.WriteLine($"  {(dryRun ? "would remove" : "removed     ")} {item.Hash}  (orphan; was {item.Path})");
                    }
                    else
                    {
                        failed++;
                        stdout.WriteLine($"  FAILED       {item.Hash}  ({orphanErr})");
                    }

                    break;

                case GcDisposition.Unknown when item.Removed:
                    freed += SizeOf(dataDir, item.Hash);
                    if (TryRemove(dataDir, item.Hash, dryRun, out var unknownErr))
                    {
                        removed++;
                        stdout.WriteLine($"  {(dryRun ? "would remove" : "removed     ")} {item.Hash}  (unknown origin)");
                    }
                    else
                    {
                        failed++;
                        stdout.WriteLine($"  FAILED       {item.Hash}  ({unknownErr})");
                    }

                    break;

                case GcDisposition.Unknown:
                    unknownKept++;
                    stdout.WriteLine($"  keep         {item.Hash}  (unknown origin; pass --prune-unknown to remove)");
                    break;
            }
        }

        if (!dryRun && removed > 0)
        {
            ProjectRegistry.Save(dataDir, surviving);
        }

        var prefix = dryRun ? "[dry-run] " : string.Empty;
        var failedNote = failed > 0 ? $", {failed} failed (in use?)" : string.Empty;
        stdout.WriteLine(
            $"{prefix}{removed} removed, {live} live, {unknownKept} unknown kept{failedNote}; ~{freed / 1024} KB freed.");
        return Task.FromResult(0);
    }

    private static long SizeOf(string dataDir, string hash)
    {
        long total = 0;
        foreach (var ext in Sidecars)
        {
            var path = Path.Combine(dataDir, hash + ext);
            if (File.Exists(path))
            {
                total += new FileInfo(path).Length;
            }
        }

        return total;
    }

    private static bool TryRemove(string dataDir, string hash, bool dryRun, out string error)
    {
        error = string.Empty;
        if (dryRun)
        {
            return true;
        }

        try
        {
            foreach (var ext in Sidecars)
            {
                var path = Path.Combine(dataDir, hash + ext);
                if (File.Exists(path))
                {
                    File.Delete(path);
                }
            }

            return true;
        }
        catch (IOException ex)
        {
            error = ex.Message;
            return false;
        }
        catch (UnauthorizedAccessException ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
