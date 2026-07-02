namespace CCStash.Core.Install;

/// <summary>
/// Wires (or unwires) CCStash's hooks/MCP server into one agent's config. Implementations are pure
/// filesystem config writers — no console I/O — so the CLI's TUI and flag paths can both drive them
/// identically via <see cref="Plan"/>/<see cref="Apply"/>.
/// </summary>
public interface IAgentAdapter
{
    /// <summary>Short, stable identifier used on the command line (e.g. <c>"claude"</c>).</summary>
    string Id { get; }

    /// <summary>Human-readable name for TUI/plan display (e.g. <c>"Claude Code"</c>).</summary>
    string DisplayName { get; }

    /// <summary>Whether this adapter can install at the given scope.</summary>
    bool SupportsScope(InstallScope scope);

    /// <summary>Whether this agent appears to already be present/configured for <paramref name="ctx"/>.</summary>
    bool Detect(InstallContext ctx);

    /// <summary>Compute the actions <see cref="Apply"/> would take, without writing anything.</summary>
    InstallPlan Plan(InstallContext ctx);

    /// <summary>Write the config changes described by (a freshly recomputed) <paramref name="plan"/>.</summary>
    void Apply(InstallPlan plan, InstallContext ctx);

    /// <summary>Remove only the CCStash-authored entries this adapter would have written for <paramref name="ctx"/>.</summary>
    void Remove(InstallContext ctx);
}
