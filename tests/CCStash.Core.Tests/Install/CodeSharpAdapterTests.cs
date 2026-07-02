// tests/CCStash.Core.Tests/Install/CodeSharpAdapterTests.cs
using CCStash.Core.Install;

namespace CCStash.Core.Tests.Install;

public class CodeSharpAdapterTests : IDisposable
{
    private readonly string _projectDir;

    public CodeSharpAdapterTests()
    {
        _projectDir = Path.Combine(Path.GetTempPath(), $"ccstash-cs-proj-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_projectDir);
    }

    public void Dispose()
    {
        if (Directory.Exists(_projectDir))
        {
            Directory.Delete(_projectDir, recursive: true);
        }
    }

    [Fact]
    public void SupportsScope_is_project_only_in_SP1()
    {
        var adapter = new CodeSharpAdapter();

        Assert.True(adapter.SupportsScope(InstallScope.Project));
        Assert.False(adapter.SupportsScope(InstallScope.User));
    }

    [Fact]
    public void Apply_writes_appsettings_mcp_entry_and_skill_file()
    {
        var adapter = new CodeSharpAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);

        adapter.Apply(adapter.Plan(ctx), ctx);

        var appsettings = JsonConfigEditor.LoadOrEmpty(Path.Combine(_projectDir, "appsettings.json"));
        var server = appsettings["CodeSharp"]!["Mcp"]!["Servers"]!["ccstash"]!;
        Assert.Equal("stdio", (string?)server["Transport"]);
        Assert.Equal("dotnet", (string?)server["Command"]);
        var args = server["Args"]!.AsArray().Select(n => (string?)n).ToList();
        Assert.Equal(new[] { "dnx", "CCStash", "--", "mcp", "--project", Path.GetFullPath(_projectDir) }, args);

        var skillPath = Path.Combine(_projectDir, ".codesharp", "skills", CodeSharpAdapter.SkillFileName);
        Assert.True(File.Exists(skillPath));
        var content = File.ReadAllText(skillPath);
        Assert.Contains("event: SessionStart", content);
        Assert.Contains("dotnet dnx CCStash -- pointer --project {ProjectDir}", content);
        Assert.Contains("event: SessionEnd", content);
        Assert.Contains("dotnet dnx CCStash -- stash --transcript {TranscriptPath} --project {ProjectDir}", content);
    }

    [Fact]
    public void Plan_reports_already_present_after_apply()
    {
        var adapter = new CodeSharpAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);
        adapter.Apply(adapter.Plan(ctx), ctx);

        var plan = adapter.Plan(ctx);

        Assert.All(plan.Actions, a => Assert.True(a.AlreadyPresent));
    }

    [Fact]
    public void Remove_deletes_skill_file_and_mcp_entry_only()
    {
        var adapter = new CodeSharpAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);
        adapter.Apply(adapter.Plan(ctx), ctx);
        var appsettingsPath = Path.Combine(_projectDir, "appsettings.json");
        var appsettings = JsonConfigEditor.LoadOrEmpty(appsettingsPath);
        JsonConfigEditor.GetOrCreateObject(JsonConfigEditor.GetOrCreateObject(appsettings, "CodeSharp"), "Other")["Key"] = "keep-me";
        JsonConfigEditor.Save(appsettingsPath, appsettings);

        adapter.Remove(ctx);

        Assert.False(File.Exists(Path.Combine(_projectDir, ".codesharp", "skills", CodeSharpAdapter.SkillFileName)));
        var after = JsonConfigEditor.LoadOrEmpty(appsettingsPath);
        Assert.Null(after["CodeSharp"]?["Mcp"]?["Servers"]?["ccstash"]);
        Assert.Equal("keep-me", (string?)after["CodeSharp"]?["Other"]?["Key"]);
    }

    [Fact]
    public void Detect_true_when_codesharp_dir_or_appsettings_section_exists()
    {
        var adapter = new CodeSharpAdapter();
        var ctx = new InstallContext(InstallScope.Project, _projectDir);
        Assert.False(adapter.Detect(ctx));

        Directory.CreateDirectory(Path.Combine(_projectDir, ".codesharp"));
        Assert.True(adapter.Detect(ctx));
    }
}
