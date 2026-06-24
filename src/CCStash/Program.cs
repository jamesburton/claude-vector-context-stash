using CCStash.Verbs;

// Minimal verb dispatch. (The plan specifies System.CommandLine; manual dispatch is used here
// to avoid a pre-release dependency — the verb contract is identical.)
if (args.Length == 0)
{
    Console.Error.WriteLine("Usage: ccstash <stash|pointer|status|search> [args]");
    return 1;
}

var cwd = Environment.CurrentDirectory;

return args[0] switch
{
    "stash" => await StashVerb.RunAsync(Console.In),
    "pointer" => await PointerVerb.RunAsync(Console.In, Console.Out),
    "status" => await StatusVerb.RunAsync(cwd, Console.Out),
    "search" => await SearchVerb.RunAsync(cwd, args.Length > 1 ? string.Join(' ', args[1..]) : string.Empty, Console.Out),
    "mcp" => await McpVerb.RunAsync(cwd),
    "init" => await InitVerb.RunAsync(cwd, Console.Out),
    _ => Unknown(args[0]),
};

static int Unknown(string verb)
{
    Console.Error.WriteLine($"Unknown verb: {verb}");
    return 1;
}
