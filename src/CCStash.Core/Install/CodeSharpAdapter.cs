// src/CCStash.Core/Install/CodeSharpAdapter.cs
using System.Text.Json.Nodes;

namespace CCStash.Core.Install;

/// <summary>
/// Wires CCStash into CodeSharp (project scope only in SP1): an <c>appsettings.json</c>
/// <c>CodeSharp:Mcp:Servers:ccstash</c> entry, and a <c>.codesharp/skills/ccstash.md</c> hook-skill
/// with <c>SessionStart</c>/<c>SessionEnd</c> command hooks. The <c>SessionEnd</c> hook references a
/// <c>{TranscriptPath}</c> placeholder CodeSharp does not yet provide (SP2); until then it runs and
/// finds no transcript, which is hook-safe (<c>stash</c> always exits 0).
/// </summary>
public sealed class CodeSharpAdapter : IAgentAdapter
{
    /// <summary>File name of the generated hook-skill under <c>.codesharp/skills/</c>.</summary>
    public const string SkillFileName = "ccstash.md";

    private const string PointerCommandTemplate = "dotnet dnx CCStash -- pointer --project {ProjectDir}";
    private const string StashCommandTemplate = "dotnet dnx CCStash -- stash --transcript {TranscriptPath} --project {ProjectDir}";

    /// <inheritdoc />
    public string Id => "codesharp";

    /// <inheritdoc />
    public string DisplayName => "CodeSharp";

    /// <inheritdoc />
    public bool SupportsScope(InstallScope scope) => scope == InstallScope.Project;

    /// <inheritdoc />
    public bool Detect(InstallContext ctx)
    {
        if (Directory.Exists(Path.Combine(ctx.ProjectDir, ".codesharp")))
        {
            return true;
        }

        var appsettings = JsonConfigEditor.LoadOrEmpty(AppSettingsPath(ctx));
        return appsettings["CodeSharp"] is not null;
    }

    /// <inheritdoc />
    public InstallPlan Plan(InstallContext ctx)
    {
        RequireProjectScope(ctx);

        var appsettingsPath = AppSettingsPath(ctx);
        var appsettings = JsonConfigEditor.LoadOrEmpty(appsettingsPath);
        var servers = McpServersObject(appsettings);
        var mcpPresent = JsonConfigEditor.EnsureChild(servers, "ccstash", McpServerNode(ctx));

        var skillPath = SkillPath(ctx);
        var skillPresent = File.Exists(skillPath) && File.ReadAllText(skillPath) == SkillFileContent;

        return new InstallPlan(Id, new List<InstallAction>
        {
            new($"{appsettingsPath} CodeSharp:Mcp:Servers:ccstash", "Register the ccstash MCP server", mcpPresent),
            new(skillPath, "Write the ccstash SessionStart/SessionEnd hook-skill", skillPresent),
        });
    }

    /// <inheritdoc />
    public void Apply(InstallPlan plan, InstallContext ctx)
    {
        RequireProjectScope(ctx);

        var appsettingsPath = AppSettingsPath(ctx);
        var appsettings = JsonConfigEditor.LoadOrEmpty(appsettingsPath);
        var servers = McpServersObject(appsettings);
        JsonConfigEditor.EnsureChild(servers, "ccstash", McpServerNode(ctx));
        JsonConfigEditor.Save(appsettingsPath, appsettings);

        var skillPath = SkillPath(ctx);
        Directory.CreateDirectory(Path.GetDirectoryName(skillPath)!);
        File.WriteAllText(skillPath, SkillFileContent);
    }

    /// <inheritdoc />
    public void Remove(InstallContext ctx)
    {
        RequireProjectScope(ctx);

        var appsettingsPath = AppSettingsPath(ctx);
        if (File.Exists(appsettingsPath))
        {
            var appsettings = JsonConfigEditor.LoadOrEmpty(appsettingsPath);
            if (appsettings["CodeSharp"]?["Mcp"]?["Servers"] is JsonObject servers)
            {
                JsonConfigEditor.RemoveChild(servers, "ccstash");
            }

            JsonConfigEditor.Save(appsettingsPath, appsettings);
        }

        var skillPath = SkillPath(ctx);
        if (File.Exists(skillPath))
        {
            File.Delete(skillPath);
        }
    }

    private static void RequireProjectScope(InstallContext ctx)
    {
        if (ctx.Scope != InstallScope.Project)
        {
            throw new NotSupportedException("CodeSharpAdapter supports project scope only in SP1.");
        }
    }

    private static JsonObject McpServersObject(JsonObject appsettings) =>
        JsonConfigEditor.GetOrCreateObject(
            JsonConfigEditor.GetOrCreateObject(
                JsonConfigEditor.GetOrCreateObject(appsettings, "CodeSharp"), "Mcp"), "Servers");

    private static JsonObject McpServerNode(InstallContext ctx) => new()
    {
        ["Transport"] = "stdio",
        ["Command"] = "dotnet",
        ["Args"] = new JsonArray("dnx", "CCStash", "--", "mcp", "--project", Path.GetFullPath(ctx.ProjectDir)),
    };

    private static string AppSettingsPath(InstallContext ctx) => Path.Combine(ctx.ProjectDir, "appsettings.json");

    private static string SkillPath(InstallContext ctx) => Path.Combine(ctx.ProjectDir, ".codesharp", "skills", SkillFileName);

    private static string SkillFileContent =>
        "---\n" +
        "hooks:\n" +
        "  - event: SessionStart\n" +
        "    handler:\n" +
        "      type: command\n" +
        $"      command: \"{PointerCommandTemplate}\"\n" +
        "  - event: SessionEnd\n" +
        "    handler:\n" +
        "      type: command\n" +
        $"      command: \"{StashCommandTemplate}\"\n" +
        "---\n\n" +
        "# CCStash\n\n" +
        "Wires CCStash's pointer/stash hooks into CodeSharp. Managed by `ccstash install` — do not " +
        "edit by hand; run `ccstash uninstall --agent codesharp` to remove.\n";
}
