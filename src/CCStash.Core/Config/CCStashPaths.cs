using System.Security.Cryptography;
using System.Text;

namespace CCStash.Core.Config;

/// <summary>Resolves CCStash's on-disk locations under the user profile.</summary>
public static class CCStashPaths
{
    /// <summary>Root data directory (<c>~/.claude/ccstash</c>).</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude", "ccstash");

    /// <summary>Path to the global config file.</summary>
    public static string ConfigPath => Path.Combine(DataDir, "config.json");

    /// <summary>Path to the log file.</summary>
    public static string LogPath => Path.Combine(DataDir, "ccstash.log");

    /// <summary>A stable, filename-safe hash identifying a project directory.</summary>
    public static string ProjectHash(string cwd)
    {
        // Canonicalize separators and case so the same project maps to one db regardless of
        // whether the path arrives with '/' (hook JSON) or '\' (Environment.CurrentDirectory).
        var normalized = cwd.Replace('\\', '/').TrimEnd('/').ToLowerInvariant();
        var bytes = SHA256.HashData(Encoding.UTF8.GetBytes(normalized));
        return Convert.ToHexString(bytes)[..16].ToLowerInvariant();
    }

    /// <summary>Path to the per-project sqlite database.</summary>
    public static string DbPath(string cwd) => Path.Combine(DataDir, $"{ProjectHash(cwd)}.db");

    /// <summary>
    /// Path to the user-scoped Claude Code settings file (<c>~/.claude/settings.json</c>), used by
    /// <c>install --scope user</c> for the Claude Code adapter.
    /// </summary>
    public static string ClaudeUserSettingsPath => Path.Combine(HomeDir, ".claude", "settings.json");

    /// <summary>
    /// Path to Claude Code's user-scoped MCP registry (<c>~/.claude.json</c>), used by
    /// <c>install --scope user</c> for the Claude Code adapter.
    /// </summary>
    public static string ClaudeUserJsonPath => Path.Combine(HomeDir, ".claude.json");

    /// <summary>
    /// Resolves the user's home directory, honoring <c>CCSTASH_HOME_OVERRIDE</c> when set. The
    /// override exists purely as a test seam so user-scope installer tests never touch the real
    /// <c>~</c>; production code paths never set this variable.
    /// </summary>
    private static string HomeDir =>
        Environment.GetEnvironmentVariable("CCSTASH_HOME_OVERRIDE")
        ?? Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
}
