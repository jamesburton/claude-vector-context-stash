namespace CCStash.Core.Install;

/// <summary>Where an agent's CCStash config should be installed.</summary>
public enum InstallScope
{
    /// <summary>Config lives inside the target project (e.g. <c>.claude/settings.json</c>).</summary>
    Project,

    /// <summary>Config lives under the user's home directory (e.g. <c>~/.claude/settings.json</c>).</summary>
    User,
}

/// <summary>The scope and project directory an <see cref="IAgentAdapter"/> operation runs against.</summary>
/// <param name="Scope">Project or user scope.</param>
/// <param name="ProjectDir">
/// The target project's root directory. Used verbatim for <see cref="InstallScope.Project"/> writes,
/// and to derive the absolute path baked into MCP server args either way.
/// </param>
public sealed record InstallContext(InstallScope Scope, string ProjectDir);

/// <summary>One concrete config change an adapter will make (or already made), for dry-run/plan display.</summary>
/// <param name="Target">A human-readable file/key path identifying what this action touches.</param>
/// <param name="Description">A human-readable summary of the change.</param>
/// <param name="AlreadyPresent">True if this action is already satisfied by existing config (a no-op on Apply).</param>
public sealed record InstallAction(string Target, string Description, bool AlreadyPresent);

/// <summary>The full set of actions an adapter will take for one <see cref="InstallContext"/>.</summary>
/// <param name="Agent">The owning adapter's <see cref="IAgentAdapter.Id"/>.</param>
/// <param name="Actions">The individual actions, in application order.</param>
public sealed record InstallPlan(string Agent, IReadOnlyList<InstallAction> Actions);
