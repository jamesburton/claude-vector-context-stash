using System.Text.Json;
using System.Text.Json.Nodes;
using CCStash.Core.Config;

namespace CCStash.Verbs;

/// <summary>
/// Handles the <c>init</c> verb: wires CCStash hooks and the MCP server into the project's
/// Claude Code config (<c>.claude/settings.json</c> and <c>.mcp.json</c>), and writes a default
/// global config. Existing entries are merged, not clobbered.
/// </summary>
internal static class InitVerb
{
    private const string StashCmd = "dnx -y CCStash -- stash";
    private const string PointerCmd = "dnx -y CCStash -- pointer";

    /// <summary>Run the init wiring for the given project directory.</summary>
    public static Task<int> RunAsync(string cwd, TextWriter stdout)
    {
        Directory.CreateDirectory(CCStashPaths.DataDir);
        if (!File.Exists(CCStashPaths.ConfigPath))
        {
            File.WriteAllText(
                CCStashPaths.ConfigPath,
                JsonSerializer.Serialize(new CCStashConfig(), new JsonSerializerOptions { WriteIndented = true }));
        }

        var settingsPath = Path.Combine(cwd, ".claude", "settings.json");
        WriteHooks(settingsPath);

        var mcpPath = Path.Combine(cwd, ".mcp.json");
        WriteMcp(mcpPath);

        stdout.WriteLine($"CCStash initialized for {cwd}:");
        stdout.WriteLine($"  hooks   -> {settingsPath} (PreCompact: stash, SessionStart[compact]: pointer)");
        stdout.WriteLine($"  mcp     -> {mcpPath} (ccstash: retrieve_context)");
        stdout.WriteLine($"  config  -> {CCStashPaths.ConfigPath}");
        stdout.WriteLine("Place a local model at ~/.claude/ccstash/models/all-MiniLM-L6-v2/ (model.onnx + tokenizer.json)");
        stdout.WriteLine("for semantic embeddings; otherwise a non-semantic fallback is used. Restart Claude Code to load.");
        return Task.FromResult(0);
    }

    private static void WriteHooks(string settingsPath)
    {
        var root = LoadObject(settingsPath);
        var hooks = root["hooks"] as JsonObject ?? new JsonObject();
        root["hooks"] = hooks;

        EnsureHook(hooks, "PreCompact", matcher: null, StashCmd);
        EnsureHook(hooks, "SessionStart", matcher: "compact", PointerCmd);

        Save(settingsPath, root);
    }

    private static void WriteMcp(string mcpPath)
    {
        var root = LoadObject(mcpPath);
        var servers = root["mcpServers"] as JsonObject ?? new JsonObject();
        root["mcpServers"] = servers;
        servers["ccstash"] = new JsonObject
        {
            ["command"] = "dnx",
            ["args"] = new JsonArray("-y", "CCStash", "--", "mcp"),
        };
        Save(mcpPath, root);
    }

    /// <summary>Ensure an event has a hook entry running <paramref name="command"/>; append if absent.</summary>
    private static void EnsureHook(JsonObject hooks, string eventName, string? matcher, string command)
    {
        var arr = hooks[eventName] as JsonArray ?? new JsonArray();
        hooks[eventName] = arr;

        var alreadyPresent = arr.OfType<JsonObject>()
            .SelectMany(e => (e["hooks"] as JsonArray ?? new JsonArray()).OfType<JsonObject>())
            .Any(h => (string?)h["command"] == command);
        if (alreadyPresent)
        {
            return;
        }

        var entry = new JsonObject
        {
            ["hooks"] = new JsonArray(new JsonObject { ["type"] = "command", ["command"] = command }),
        };
        if (matcher is not null)
        {
            entry["matcher"] = matcher;
        }

        arr.Add(entry);
    }

    private static JsonObject LoadObject(string path)
    {
        if (!File.Exists(path))
        {
            return new JsonObject();
        }

        try
        {
            return JsonNode.Parse(File.ReadAllText(path))?.AsObject() ?? new JsonObject();
        }
        catch (JsonException)
        {
            return new JsonObject();
        }
    }

    private static void Save(string path, JsonObject root)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
}
