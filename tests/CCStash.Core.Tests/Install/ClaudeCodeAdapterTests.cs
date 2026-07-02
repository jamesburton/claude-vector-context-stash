using System.Text.Json.Nodes;
using CCStash.Core.Config;
using CCStash.Core.Install;

namespace CCStash.Core.Tests.Install;

public class ClaudeCodeAdapterTests : IDisposable
{
    private readonly string _projectDir;
    private readonly string _fakeHome;

    public ClaudeCodeAdapterTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"ccstash-cc-proj-{Guid.NewGuid():N}");
        _fakeHome = Path.Combine(Path.GetTempPath(), $"ccstash-cc-home-{Guid.NewGuid():N}");
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
    public void SupportsScope_returns_true_for_both_scopes()
    {
        var adapter = new ClaudeCodeAdapter();

        Assert.True(adapter.SupportsScope(InstallScope.Project));
        Assert.True(adapter.SupportsScope(InstallScope.User));
    }

    [Fact]
    public void Plan_reports_not_present_before_apply_and_present_after()
    {
        var adapter = new ClaudeCodeAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);

        var before = adapter.Plan(ctx);
        Assert.All(before.Actions, a => Assert.False(a.AlreadyPresent));

        adapter.Apply(before, ctx);
        var after = adapter.Plan(ctx);
        Assert.All(after.Actions, a => Assert.True(a.AlreadyPresent));
    }

    [Fact]
    public void Apply_project_scope_writes_hooks_and_mcp_with_absolute_project_path()
    {
        var adapter = new ClaudeCodeAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);

        adapter.Apply(adapter.Plan(ctx), ctx);

        var settings = JsonConfigEditor.LoadOrEmpty(Path.Combine(_projectDir, ".claude", "settings.json"));
        var hooks = settings["hooks"]!.AsObject();
        Assert.Equal(ClaudeCodeAdapter.StashCommand, (string?)hooks["PreCompact"]![0]!["hooks"]![0]!["command"]);
        Assert.Equal(ClaudeCodeAdapter.PointerCommand, (string?)hooks["SessionStart"]![0]!["hooks"]![0]!["command"]);
        Assert.Equal("compact", (string?)hooks["SessionStart"]![0]!["matcher"]);

        var mcp = JsonConfigEditor.LoadOrEmpty(Path.Combine(_projectDir, ".mcp.json"));
        var server = mcp["mcpServers"]!["ccstash"]!;
        Assert.Equal("dnx", (string?)server["command"]);
        var args = server["args"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Equal(new[] { "-y", "CCStash", "--", "mcp", "--project", Path.GetFullPath(_projectDir) }, args);
    }

    [Fact]
    public void Apply_user_scope_writes_to_home_settings_and_claude_json_without_project_arg()
    {
        var adapter = new ClaudeCodeAdapter();
        var ctx = new InstallContext(InstallScope.User, _projectDir);

        adapter.Apply(adapter.Plan(ctx), ctx);

        var settings = JsonConfigEditor.LoadOrEmpty(CCStashPaths.ClaudeUserSettingsPath);
        Assert.Equal(
            ClaudeCodeAdapter.StashCommand,
            (string?)settings["hooks"]!["PreCompact"]![0]!["hooks"]![0]!["command"]);

        var root = JsonConfigEditor.LoadOrEmpty(CCStashPaths.ClaudeUserJsonPath);
        var args = root["mcpServers"]!["ccstash"]!["args"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Equal(new[] { "-y", "CCStash", "--", "mcp" }, args);
    }

    [Fact]
    public void Apply_preserves_unrelated_existing_hooks_and_mcp_entries()
    {
        var adapter = new ClaudeCodeAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);
        var settingsPath = Path.Combine(_projectDir, ".claude", "settings.json");
        JsonConfigEditor.Save(settingsPath, new JsonObject
        {
            ["hooks"] = new JsonObject
            {
                ["PreCompact"] = new JsonArray(new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "some-other-tool" }),
                }),
            },
            ["unrelatedTopLevelKey"] = "keep-me",
        });

        adapter.Apply(adapter.Plan(ctx), ctx);

        var settings = JsonConfigEditor.LoadOrEmpty(settingsPath);
        Assert.Equal("keep-me", (string?)settings["unrelatedTopLevelKey"]);
        var preCompactCommands = settings["hooks"]!["PreCompact"]!.AsArray()
            .Select(e => (string?)e!["hooks"]![0]!["command"]).ToList();
        Assert.Contains("some-other-tool", preCompactCommands);
        Assert.Contains(ClaudeCodeAdapter.StashCommand, preCompactCommands);
    }

    [Fact]
    public void Remove_deletes_only_ccstash_entries()
    {
        var adapter = new ClaudeCodeAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);
        adapter.Apply(adapter.Plan(ctx), ctx);
        var settingsPath = Path.Combine(_projectDir, ".claude", "settings.json");
        var settings = JsonConfigEditor.LoadOrEmpty(settingsPath);
        JsonConfigEditor.GetOrCreateObject(settings, "hooks");
        JsonConfigEditor.EnsureArrayEntry(
            settings["hooks"]!.AsObject(), "PreCompact",
            e => (string?)e["hooks"]![0]!["command"] == "some-other-tool",
            () => new JsonObject { ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = "some-other-tool" }) });
        settings["unrelatedTopLevelKey"] = "keep-me";
        JsonConfigEditor.Save(settingsPath, settings);

        adapter.Remove(ctx);

        var after = JsonConfigEditor.LoadOrEmpty(settingsPath);
        var preCompactCommands = after["hooks"]!["PreCompact"]!.AsArray()
            .Select(e => (string?)e!["hooks"]![0]!["command"]).ToList();
        Assert.DoesNotContain(ClaudeCodeAdapter.StashCommand, preCompactCommands);
        Assert.Contains("some-other-tool", preCompactCommands);
        Assert.Equal("keep-me", (string?)after["unrelatedTopLevelKey"]);
        var mcp = JsonConfigEditor.LoadOrEmpty(Path.Combine(_projectDir, ".mcp.json"));
        Assert.Null(mcp["mcpServers"]?["ccstash"]);
    }

    [Fact]
    public void Detect_is_false_for_a_fresh_project_and_true_after_apply()
    {
        var adapter = new ClaudeCodeAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);

        Assert.False(adapter.Detect(ctx));
        adapter.Apply(adapter.Plan(ctx), ctx);
        Assert.True(adapter.Detect(ctx));
    }
}
