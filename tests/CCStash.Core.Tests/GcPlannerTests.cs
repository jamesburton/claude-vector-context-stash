using CCStash.Core.Config;
using CCStash.Core.Maintenance;

namespace CCStash.Core.Tests;

public class GcPlannerTests
{
    private static ProjectEntry Entry(string path) => new(path, "2026-06-24T00:00:00+00:00");

    [Fact]
    public void Live_project_is_kept()
    {
        var manifest = new Dictionary<string, ProjectEntry> { ["aaaa"] = Entry("C:/exists") };

        var plan = GcPlanner.Plan(["aaaa"], manifest, p => p == "C:/exists", pruneUnknown: false);

        var item = Assert.Single(plan);
        Assert.Equal(GcDisposition.Live, item.Disposition);
        Assert.False(item.Removed);
    }

    [Fact]
    public void Missing_project_directory_is_an_orphan_and_removed()
    {
        var manifest = new Dictionary<string, ProjectEntry> { ["bbbb"] = Entry("C:/gone") };

        var plan = GcPlanner.Plan(["bbbb"], manifest, _ => false, pruneUnknown: false);

        var item = Assert.Single(plan);
        Assert.Equal(GcDisposition.Orphan, item.Disposition);
        Assert.True(item.Removed);
        Assert.Equal("C:/gone", item.Path);
    }

    [Fact]
    public void Unattributed_db_is_kept_by_default()
    {
        var plan = GcPlanner.Plan(["cccc"], new Dictionary<string, ProjectEntry>(), _ => true, pruneUnknown: false);

        var item = Assert.Single(plan);
        Assert.Equal(GcDisposition.Unknown, item.Disposition);
        Assert.False(item.Removed);
    }

    [Fact]
    public void Unattributed_db_is_removed_only_with_prune_unknown()
    {
        var plan = GcPlanner.Plan(["cccc"], new Dictionary<string, ProjectEntry>(), _ => true, pruneUnknown: true);

        var item = Assert.Single(plan);
        Assert.Equal(GcDisposition.Unknown, item.Disposition);
        Assert.True(item.Removed);
    }

    [Fact]
    public void Never_removes_a_db_whose_project_still_exists()
    {
        // The cross-project safety guarantee: any number of known projects, all of which exist,
        // must survive gc — even with prune-unknown on.
        var manifest = new Dictionary<string, ProjectEntry>
        {
            ["p1"] = Entry("C:/a"),
            ["p2"] = Entry("C:/b"),
            ["p3"] = Entry("C:/c"),
        };

        var plan = GcPlanner.Plan(["p1", "p2", "p3"], manifest, _ => true, pruneUnknown: true);

        Assert.All(plan, i => Assert.False(i.Removed));
        Assert.All(plan, i => Assert.Equal(GcDisposition.Live, i.Disposition));
    }
}
