using CCStash.Core.Config;

namespace CCStash.Core.Tests;

public class ProjectRegistryTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"ccstash-reg-{Guid.NewGuid():N}");

    [Fact]
    public void Load_returns_empty_when_manifest_is_absent()
    {
        Assert.Empty(ProjectRegistry.Load(_dir));
    }

    [Fact]
    public void Record_then_load_resolves_the_project_by_its_hash()
    {
        var cwd = "C:/Development/my-project";
        ProjectRegistry.Record(_dir, cwd, "2026-06-24T10:00:00+00:00");

        var entries = ProjectRegistry.Load(_dir);

        var hash = CCStashPaths.ProjectHash(cwd);
        Assert.True(entries.ContainsKey(hash));
        Assert.Equal(cwd, entries[hash].Path);
        Assert.Equal("2026-06-24T10:00:00+00:00", entries[hash].LastStash);
    }

    [Fact]
    public void Record_upserts_without_dropping_other_projects()
    {
        ProjectRegistry.Record(_dir, "C:/proj-a", "2026-06-24T10:00:00+00:00");
        ProjectRegistry.Record(_dir, "C:/proj-b", "2026-06-24T11:00:00+00:00");
        // Re-record A with a newer timestamp.
        ProjectRegistry.Record(_dir, "C:/proj-a", "2026-06-24T12:00:00+00:00");

        var entries = ProjectRegistry.Load(_dir);

        Assert.Equal(2, entries.Count);
        Assert.Equal("2026-06-24T12:00:00+00:00", entries[CCStashPaths.ProjectHash("C:/proj-a")].LastStash);
        Assert.Equal("C:/proj-b", entries[CCStashPaths.ProjectHash("C:/proj-b")].Path);
    }

    public void Dispose()
    {
        if (Directory.Exists(_dir))
        {
            Directory.Delete(_dir, recursive: true);
        }
    }
}
