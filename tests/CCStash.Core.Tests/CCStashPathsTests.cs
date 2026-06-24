using CCStash.Core.Config;

namespace CCStash.Core.Tests;

public class CCStashPathsTests
{
    [Fact]
    public void ProjectHash_is_stable_and_path_safe()
    {
        var h1 = CCStashPaths.ProjectHash(@"C:\Dev\Foo");
        var h2 = CCStashPaths.ProjectHash(@"C:\Dev\Foo");

        Assert.Equal(h1, h2);
        Assert.DoesNotContain(h1, c => Path.GetInvalidFileNameChars().Contains(c));
        Assert.EndsWith(".db", CCStashPaths.DbPath(@"C:\Dev\Foo"));

        // Separator and trailing-slash insensitivity: hook cwd vs Environment.CurrentDirectory.
        Assert.Equal(CCStashPaths.ProjectHash(@"C:\Dev\Foo"), CCStashPaths.ProjectHash("C:/Dev/Foo/"));
    }

    [Fact]
    public void Config_defaults_load_when_file_missing()
    {
        var cfg = CCStashConfig.Load(Path.Combine(Path.GetTempPath(), $"missing-{Guid.NewGuid():N}.json"));

        Assert.Equal("sqlite", cfg.Store);
        Assert.Equal(6, cfg.RetrievalLimit);
        Assert.False(cfg.ProjectWide);
    }
}
