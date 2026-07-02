using System.Text.Json.Nodes;
using CCStash.Core.Config;
using CCStash.Core.Install;
using CCStash.Verbs;

namespace CCStash.Tests;

public class InstallVerbTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string _fakeHome;
    private readonly StringWriter _stdout = new();
    private readonly StringWriter _stderr = new();

    public InstallVerbTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"ccstash-installverb-{Guid.NewGuid():N}");
        _fakeHome = Path.Combine(Path.GetTempPath(), $"ccstash-installverb-home-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectDir);
        Environment.SetEnvironmentVariable("CCSTASH_HOME_OVERRIDE", _fakeHome);
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("CCSTASH_HOME_OVERRIDE", null);
        if (Directory.Exists(_projectDir))
        {
            Directory.Delete(_projectDir, recursive: true);
        }

        if (Directory.Exists(_fakeHome))
        {
            Directory.Delete(_fakeHome, recursive: true);
        }
    }

    [Fact]
    public async Task Install_all_agents_project_scope_yes_writes_expected_files()
    {
        var args = new[] { "--agent", "all", "--scope", "project", "--project", _projectDir, "--yes" };

        var exit = await InstallVerb.RunAsync(args, _projectDir, new StringReader(string.Empty), _stdout, _stderr);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(_projectDir, ".claude", "settings.json")));
        Assert.True(File.Exists(Path.Combine(_projectDir, ".mcp.json")));
        Assert.True(File.Exists(Path.Combine(_projectDir, "appsettings.json")));
        Assert.True(File.Exists(Path.Combine(_projectDir, ".codesharp", "skills", CodeSharpAdapter.SkillFileName)));
    }

    [Fact]
    public async Task Install_dry_run_writes_nothing()
    {
        var args = new[] { "--agent", "claude", "--scope", "project", "--project", _projectDir, "--dry-run" };

        var exit = await InstallVerb.RunAsync(args, _projectDir, new StringReader(string.Empty), _stdout, _stderr);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(_projectDir, ".claude", "settings.json")));
        Assert.Contains("dry-run", _stdout.ToString());
    }

    [Fact]
    public async Task Install_without_yes_aborts_on_declined_confirm()
    {
        var args = new[] { "--agent", "claude", "--scope", "project", "--project", _projectDir };

        var exit = await InstallVerb.RunAsync(args, _projectDir, new StringReader("n\n"), _stdout, _stderr);

        Assert.Equal(1, exit);
        Assert.False(File.Exists(Path.Combine(_projectDir, ".claude", "settings.json")));
    }

    [Fact]
    public async Task Install_without_yes_applies_on_confirmed_yes()
    {
        var args = new[] { "--agent", "claude", "--scope", "project", "--project", _projectDir };

        var exit = await InstallVerb.RunAsync(args, _projectDir, new StringReader("y\n"), _stdout, _stderr);

        Assert.Equal(0, exit);
        Assert.True(File.Exists(Path.Combine(_projectDir, ".claude", "settings.json")));
    }

    [Fact]
    public async Task Uninstall_removes_only_ccstash_entries_written_by_install()
    {
        var installArgs = new[] { "--agent", "all", "--scope", "project", "--project", _projectDir, "--yes" };
        await InstallVerb.RunAsync(installArgs, _projectDir, new StringReader(string.Empty), _stdout, _stderr);

        var uninstallArgs = new[] { "--agent", "all", "--scope", "project", "--project", _projectDir, "--yes" };
        var exit = await InstallVerb.RunUninstallAsync(uninstallArgs, _projectDir, new StringReader(string.Empty), _stdout, _stderr);

        Assert.Equal(0, exit);
        var mcp = JsonConfigEditor.LoadOrEmpty(Path.Combine(_projectDir, ".mcp.json"));
        Assert.Null(mcp["mcpServers"]?["ccstash"]);
        Assert.False(File.Exists(Path.Combine(_projectDir, ".codesharp", "skills", CodeSharpAdapter.SkillFileName)));
    }

    [Fact]
    public async Task Install_unknown_agent_reports_error_and_exits_nonzero()
    {
        var args = new[] { "--agent", "nope", "--scope", "project", "--project", _projectDir, "--yes" };

        var exit = await InstallVerb.RunAsync(args, _projectDir, new StringReader(string.Empty), _stdout, _stderr);

        Assert.Equal(1, exit);
        Assert.Contains("nope", _stderr.ToString());
    }

    [Fact]
    public async Task Install_user_scope_writes_under_fake_home_not_project()
    {
        var args = new[] { "--agent", "claude", "--scope", "user", "--project", _projectDir, "--yes" };

        var exit = await InstallVerb.RunAsync(args, _projectDir, new StringReader(string.Empty), _stdout, _stderr);

        Assert.Equal(0, exit);
        Assert.False(File.Exists(Path.Combine(_projectDir, ".claude", "settings.json")));
        Assert.True(File.Exists(CCStashPaths.ClaudeUserSettingsPath));
    }
}
