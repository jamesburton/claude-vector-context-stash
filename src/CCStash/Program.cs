using CCStash.Verbs;

// Minimal verb dispatch. (The plan specifies System.CommandLine; manual dispatch is used here
// to avoid a pre-release dependency — the verb contract is identical.)
if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: ccstash <stash|pointer|status|search|gc|mcp|init> [args]");
    return 1;
}

var cwd = Environment.CurrentDirectory;

return args[0] switch
{
    "stash" => await StashVerb.RunAsync(args[1..], Console.In),
    "pointer" => await PointerVerb.RunAsync(Console.In, Console.Out),
    "status" => await StatusVerb.RunAsync(cwd, Console.Out),
    "search" => await SearchVerb.RunAsync(cwd, args.Length > 1 ? string.Join(' ', args[1..]) : string.Empty, Console.Out),
    "gc" => await GcVerb.RunAsync(args[1..], Console.Out),
    "mcp" => await McpVerb.RunAsync(ResolveProject(args, cwd)),
    "init" => await InitVerb.RunAsync(cwd, Console.Out),
    _ => Unknown(args[0]),
};

static int Unknown(string verb)
{
    Console.Error.WriteLine($"Unknown verb: {verb}");
    return 1;
}

// Resolve the project directory for the MCP server, which Claude Code may launch from any cwd.
// Precedence: --project arg (baked into project .mcp.json by `init`) > CLAUDE_PROJECT_DIR (set by
// Claude Code in the server's env — the robust choice for a user-scoped server shared across
// projects) > CCSTASH_PROJECT > cwd.
static string ResolveProject(string[] args, string cwd)
{
    var i = Array.IndexOf(args, "--project");
    if (i >= 0 && i + 1 < args.Length)
    {
        return args[i + 1];
    }

    return Environment.GetEnvironmentVariable("CLAUDE_PROJECT_DIR")
        ?? Environment.GetEnvironmentVariable("CCSTASH_PROJECT")
        ?? cwd;
}
