using CCStash.Core.Config;

namespace CCStash.Core.Tests;

[Collection("CCSTASH_HOME_OVERRIDE serial")]
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

    [Fact]
    public void ClaudeUserPaths_use_home_override_when_set()
    {
        var fakeHome = Path.Combine(Path.GetTempPath(), $"ccstash-home-{Guid.NewGuid():N}");
        Environment.SetEnvironmentVariable("CCSTASH_HOME_OVERRIDE", fakeHome);
        try
        {
            Assert.Equal(Path.Combine(fakeHome, ".claude", "settings.json"), CCStashPaths.ClaudeUserSettingsPath);
            Assert.Equal(Path.Combine(fakeHome, ".claude.json"), CCStashPaths.ClaudeUserJsonPath);
        }
        finally
        {
            Environment.SetEnvironmentVariable("CCSTASH_HOME_OVERRIDE", null);
        }
    }

    [Fact]
    public void ClaudeUserPaths_fall_back_to_real_user_profile_when_unset()
    {
        Environment.SetEnvironmentVariable("CCSTASH_HOME_OVERRIDE", null);
        var expectedHome = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

        Assert.Equal(Path.Combine(expectedHome, ".claude", "settings.json"), CCStashPaths.ClaudeUserSettingsPath);
    }
}
