using CCStash.Core.Install;

namespace CCStash.Verbs;

/// <summary>
/// Handles the <c>install</c>/<c>uninstall</c> verbs: wires (or unwires) CCStash's config into one
/// or more agents, at project or user scope, via <see cref="IAgentAdapter"/> — non-interactively from
/// flags, or through a TUI when no flags are given and a terminal is attached.
/// </summary>
internal static class InstallVerb
{
    internal const string UsageLine =
        "Usage: ccstash install --agent <claude|codesharp|all> --scope <project|user> [--project <path>] [--yes] [--dry-run]";

    internal static readonly IReadOnlyList<IAgentAdapter> AllAdapters =
    [
        new ClaudeCodeAdapter(),
        new CodeSharpAdapter(),
    ];

    /// <summary>Run <c>install</c>: flag-driven when flags are present, otherwise usage/error (TUI added in a later task).</summary>
    public static Task<int> RunAsync(string[] args, string cwd, TextReader stdin, TextWriter stdout, TextWriter stderr)
    {
        if (HasActionableFlags(args))
        {
            return RunSelectedAsync(args, cwd, stdin, stdout, stderr, uninstall: false);
        }

        stderr.WriteLine(UsageLine);
        return Task.FromResult(1);
    }

    /// <summary>Run <c>uninstall</c>: same selectors as <c>install</c>, dispatching to <see cref="IAgentAdapter.Remove"/>.</summary>
    public static Task<int> RunUninstallAsync(string[] args, string cwd, TextReader stdin, TextWriter stdout, TextWriter stderr) =>
        RunSelectedAsync(args, cwd, stdin, stdout, stderr, uninstall: true);

    private static async Task<int> RunSelectedAsync(
        string[] args, string cwd, TextReader stdin, TextWriter stdout, TextWriter stderr, bool uninstall)
    {
        var requestedAgents = ParseAgents(args);
        var unknown = requestedAgents.Where(id => AllAdapters.All(a => a.Id != id)).ToList();
        if (unknown.Count > 0)
        {
            stderr.WriteLine($"Unknown agent(s): {string.Join(',', unknown)}. Known: {string.Join(',', AllAdapters.Select(a => a.Id))}");
            return 1;
        }

        var scope = ParseScope(args);
        var projectDir = ParseProject(args, cwd);
        var dryRun = args.Contains("--dry-run");
        var yes = args.Contains("--yes");

        var selected = AllAdapters.Where(a => requestedAgents.Contains(a.Id)).ToList();
        var entries = new List<(IAgentAdapter Adapter, InstallContext Ctx, InstallPlan Plan)>();
        foreach (var adapter in selected)
        {
            if (!adapter.SupportsScope(scope))
            {
                stdout.WriteLine($"  skip   {adapter.DisplayName} does not support {scope} scope");
                continue;
            }

            var ctx = new InstallContext(scope, projectDir);
            var plan = uninstall ? new InstallPlan(adapter.Id, Array.Empty<InstallAction>()) : adapter.Plan(ctx);
            entries.Add((adapter, ctx, plan));
        }

        PrintPlan(stdout, entries, uninstall);

        if (!uninstall && dryRun)
        {
            stdout.WriteLine("[dry-run] no changes written.");
            return 0;
        }

        if (!yes && !await ConfirmAsync(stdin, stdout))
        {
            stdout.WriteLine("Aborted.");
            return 1;
        }

        foreach (var (adapter, ctx, plan) in entries)
        {
            if (uninstall)
            {
                adapter.Remove(ctx);
                stdout.WriteLine($"  removed {adapter.DisplayName} ({ctx.Scope})");
            }
            else
            {
                adapter.Apply(plan, ctx);
                stdout.WriteLine($"  applied {adapter.DisplayName} ({ctx.Scope})");
            }
        }

        return 0;
    }

    private static void PrintPlan(
        TextWriter stdout,
        IReadOnlyList<(IAgentAdapter Adapter, InstallContext Ctx, InstallPlan Plan)> entries,
        bool uninstall)
    {
        foreach (var (adapter, ctx, plan) in entries)
        {
            stdout.WriteLine($"{adapter.DisplayName} ({ctx.Scope}):");
            if (uninstall)
            {
                stdout.WriteLine("  remove all ccstash-authored entries");
                continue;
            }

            foreach (var action in plan.Actions)
            {
                var status = action.AlreadyPresent ? "already present" : "will write";
                stdout.WriteLine($"  [{status}] {action.Target} — {action.Description}");
            }
        }
    }

    private static async Task<bool> ConfirmAsync(TextReader stdin, TextWriter stdout)
    {
        stdout.Write("Apply? [y/N] ");
        var line = await stdin.ReadLineAsync();
        return string.Equals(line?.Trim(), "y", StringComparison.OrdinalIgnoreCase)
            || string.Equals(line?.Trim(), "yes", StringComparison.OrdinalIgnoreCase);
    }

    private static bool HasActionableFlags(string[] args) =>
        args.Any(a => a is "--agent" or "--scope" or "--project" or "--yes" or "--dry-run");

    private static List<string> ParseAgents(string[] args)
    {
        var values = ArgValues(args, "--agent");
        if (values.Count == 0)
        {
            return AllAdapters.Select(a => a.Id).ToList();
        }

        var ids = values.SelectMany(v => v.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)).ToList();
        return ids.Contains("all", StringComparer.OrdinalIgnoreCase)
            ? AllAdapters.Select(a => a.Id).ToList()
            : ids.Select(id => id.ToLowerInvariant()).ToList();
    }

    private static InstallScope ParseScope(string[] args)
    {
        var value = ArgValue(args, "--scope");
        return string.Equals(value, "user", StringComparison.OrdinalIgnoreCase) ? InstallScope.User : InstallScope.Project;
    }

    private static string ParseProject(string[] args, string cwd) => ArgValue(args, "--project") ?? cwd;

    private static string? ArgValue(string[] args, string flag)
    {
        var i = Array.IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private static List<string> ArgValues(string[] args, string flag)
    {
        var values = new List<string>();
        for (var i = Array.IndexOf(args, flag); i >= 0 && i + 1 < args.Length; i = Array.IndexOf(args, flag, i + 1))
        {
            values.Add(args[i + 1]);
        }

        return values;
    }
}
