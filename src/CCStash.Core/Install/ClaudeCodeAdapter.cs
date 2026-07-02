using System.Text.Json.Nodes;
using CCStash.Core.Config;

namespace CCStash.Core.Install;

/// <summary>
/// Wires CCStash into Claude Code: <c>PreCompact</c>/<c>SessionStart[compact]</c> hooks and an
/// <c>ccstash</c> MCP server entry. Project scope writes <c>.claude/settings.json</c> +
/// <c>.mcp.json</c>; user scope writes <c>~/.claude/settings.json</c> + <c>~/.claude.json</c>.
/// </summary>
public sealed class ClaudeCodeAdapter : IAgentAdapter
{
    /// <summary>The exact <c>PreCompact</c> hook command. Kept stable for idempotency across versions.</summary>
    public const string StashCommand = "dnx -y CCStash -- stash";

    /// <summary>The exact <c>SessionStart[compact]</c> hook command. Kept stable for idempotency across versions.</summary>
    public const string PointerCommand = "dnx -y CCStash -- pointer";

    /// <inheritdoc />
    public string Id => "claude";

    /// <inheritdoc />
    public string DisplayName => "Claude Code";

    /// <inheritdoc />
    public bool SupportsScope(InstallScope scope) => true;

    /// <inheritdoc />
    public bool Detect(InstallContext ctx) =>
        ctx.Scope == InstallScope.Project
            ? Directory.Exists(Path.Combine(ctx.ProjectDir, ".claude")) || File.Exists(Path.Combine(ctx.ProjectDir, ".mcp.json"))
            : File.Exists(CCStashPaths.ClaudeUserJsonPath) || File.Exists(CCStashPaths.ClaudeUserSettingsPath);

    /// <inheritdoc />
    public InstallPlan Plan(InstallContext ctx)
    {
        var settingsPath = SettingsPath(ctx);
        var settings = JsonConfigEditor.LoadOrEmpty(settingsPath);
        var hooks = JsonConfigEditor.GetOrCreateObject(settings, "hooks");

        var preCompactPresent = EnsureHookEntry(hooks, "PreCompact", matcher: null, StashCommand);
        var sessionStartPresent = EnsureHookEntry(hooks, "SessionStart", matcher: "compact", PointerCommand);

        var actions = new List<InstallAction>
        {
            new($"{settingsPath} hooks.PreCompact", $"Run `{StashCommand}` on PreCompact", preCompactPresent),
            new($"{settingsPath} hooks.SessionStart[compact]", $"Run `{PointerCommand}` on SessionStart (compact)", sessionStartPresent),
        };

        var mcpPath = McpPath(ctx);
        var mcpRoot = JsonConfigEditor.LoadOrEmpty(mcpPath);
        var servers = JsonConfigEditor.GetOrCreateObject(mcpRoot, "mcpServers");
        var mcpPresent = JsonConfigEditor.EnsureChild(servers, "ccstash", McpServerNode(ctx));
        actions.Add(new($"{mcpPath} mcpServers.ccstash", $"Register the ccstash MCP server ({ctx.Scope} scope)", mcpPresent));

        return new InstallPlan(Id, actions);
    }

    /// <inheritdoc />
    public void Apply(InstallPlan plan, InstallContext ctx)
    {
        var settingsPath = SettingsPath(ctx);
        var settings = JsonConfigEditor.LoadOrEmpty(settingsPath);
        var hooks = JsonConfigEditor.GetOrCreateObject(settings, "hooks");
        EnsureHookEntry(hooks, "PreCompact", matcher: null, StashCommand);
        EnsureHookEntry(hooks, "SessionStart", matcher: "compact", PointerCommand);
        JsonConfigEditor.Save(settingsPath, settings);

        var mcpPath = McpPath(ctx);
        var mcpRoot = JsonConfigEditor.LoadOrEmpty(mcpPath);
        var servers = JsonConfigEditor.GetOrCreateObject(mcpRoot, "mcpServers");
        JsonConfigEditor.EnsureChild(servers, "ccstash", McpServerNode(ctx));
        JsonConfigEditor.Save(mcpPath, mcpRoot);
    }

    /// <inheritdoc />
    public void Remove(InstallContext ctx)
    {
        var settingsPath = SettingsPath(ctx);
        if (File.Exists(settingsPath))
        {
            var settings = JsonConfigEditor.LoadOrEmpty(settingsPath);
            if (settings["hooks"] is JsonObject hooks)
            {
                JsonConfigEditor.RemoveArrayEntries(hooks, "PreCompact", e => HasCommand(e, StashCommand));
                JsonConfigEditor.RemoveArrayEntries(hooks, "SessionStart", e => HasCommand(e, PointerCommand));
            }

            JsonConfigEditor.Save(settingsPath, settings);
        }

        var mcpPath = McpPath(ctx);
        if (File.Exists(mcpPath))
        {
            var root = JsonConfigEditor.LoadOrEmpty(mcpPath);
            if (root["mcpServers"] is JsonObject servers)
            {
                JsonConfigEditor.RemoveChild(servers, "ccstash");
            }

            JsonConfigEditor.Save(mcpPath, root);
        }
    }

    private static bool EnsureHookEntry(JsonObject hooks, string eventName, string? matcher, string command) =>
        JsonConfigEditor.EnsureArrayEntry(
            hooks,
            eventName,
            entry => HasCommand(entry, command),
            () =>
            {
                var entry = new JsonObject
                {
                    ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command }),
                };
                if (matcher is not null)
                {
                    entry["matcher"] = matcher;
                }

                return entry;
            });

    private static bool HasCommand(JsonObject hookEventEntry, string command) =>
        (hookEventEntry["hooks"] as JsonArray)?.OfType<JsonObject>().Any(h => (string?)h["command"] == command) == true;

    private static JsonObject McpServerNode(InstallContext ctx)
    {
        var args = new JsonArray("-y", "CCStash", "--", "mcp");
        if (ctx.Scope == InstallScope.Project)
        {
            args.Add("--project");
            args.Add(Path.GetFullPath(ctx.ProjectDir));
        }

        return new JsonObject { ["command"] = "dnx", ["args"] = args };
    }

    private static string SettingsPath(InstallContext ctx) =>
        ctx.Scope == InstallScope.Project
            ? Path.Combine(ctx.ProjectDir, ".claude", "settings.json")
            : CCStashPaths.ClaudeUserSettingsPath;

    private static string McpPath(InstallContext ctx) =>
        ctx.Scope == InstallScope.Project
            ? Path.Combine(ctx.ProjectDir, ".mcp.json")
            : CCStashPaths.ClaudeUserJsonPath;
}
