using System.ComponentModel;
using System.Text;
using CCStash.Core;
using CCStash.Core.Storage;
using ModelContextProtocol.Server;

namespace CCStash.Mcp;

/// <summary>Resolved scope for MCP retrieval (whether to span all sessions in the project).</summary>
/// <param name="ProjectWide">True to search every session; false to scope to the latest session.</param>
public sealed record McpToolContext(bool ProjectWide);

/// <summary>MCP tools exposing the stash to the model. Services are injected from DI per call.</summary>
[McpServerToolType]
public sealed class RetrieveContextTools
{
    /// <summary>Search the stash and return the most relevant earlier context.</summary>
    /// <param name="retrieval">Injected retrieval service.</param>
    /// <param name="store">Injected vector store (used to resolve the latest session).</param>
    /// <param name="context">Injected scope.</param>
    /// <param name="query">What to look for, in natural language.</param>
    /// <param name="limit">Maximum chunks to return.</param>
    /// <returns>A formatted block of the most relevant stashed turns.</returns>
    [McpServerTool(Name = "retrieve_context")]
    [Description("Retrieve specific earlier context (decisions, file contents, errors) that was stashed before compaction. Call this when you need detail that was summarized away.")]
    public static async Task<string> RetrieveContext(
        IRetrievalService retrieval,
        IVectorStore store,
        McpToolContext context,
        [Description("What you are looking for, in natural language.")] string query,
        [Description("Maximum number of chunks to return (default 6).")] int limit = 6)
    {
        var session = context.ProjectWide ? null : await store.GetLatestSessionAsync();
        var hits = await retrieval.RetrieveAsync(query, limit, session);
        if (hits.Count == 0)
        {
            return "No stashed context matched the query.";
        }

        var sb = new StringBuilder();
        foreach (var h in hits)
        {
            sb.AppendLine($"--- turn {h.TurnIndex} ({h.Role}, score {h.Score:F3}) ---");
            sb.AppendLine(h.Text);
            sb.AppendLine();
        }

        return sb.ToString();
    }

    /// <summary>Report how much context is currently stashed.</summary>
    /// <param name="store">Injected vector store.</param>
    /// <returns>A one-line summary of stash size and latest session.</returns>
    [McpServerTool(Name = "list_stashes")]
    [Description("Report the number of stashed context chunks available to retrieve and the latest session.")]
    public static async Task<string> ListStashes(IVectorStore store)
    {
        var total = await store.CountAsync(null);
        var latest = await store.GetLatestSessionAsync();
        return $"{total} chunks stashed; latest session: {latest ?? "(none)"}.";
    }
}
