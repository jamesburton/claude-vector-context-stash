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
}
