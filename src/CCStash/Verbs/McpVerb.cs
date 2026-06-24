using CCStash.Core;
using CCStash.Core.Config;
using CCStash.Core.Storage;
using CCStash.Mcp;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CCStash.Verbs;

/// <summary>Handles the <c>mcp</c> verb: runs the stdio MCP server for the current project.</summary>
internal static class McpVerb
{
    /// <summary>Start the MCP server, exposing retrieve_context/list_stashes over stdio.</summary>
    public static async Task<int> RunAsync(string cwd)
    {
        var cfg = CCStashConfig.Load(CCStashPaths.ConfigPath);
        var embedder = await Composition.BuildEmbedderAsync(cfg);
        var store = Composition.BuildStore(cwd, cfg);
        await store.InitializeAsync(embedder.Dimension, embedder.ModelId);
        var retrieval = new RetrievalService(embedder, store);

        var builder = Host.CreateApplicationBuilder();

        // stdout is the JSON-RPC transport — keep it clean. Route any logging to stderr only.
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

        builder.Services.AddSingleton<IVectorStore>(store);
        builder.Services.AddSingleton<IRetrievalService>(retrieval);
        builder.Services.AddSingleton(new McpToolContext(cfg.ProjectWide));

        builder.Services
            .AddMcpServer()
            .WithStdioServerTransport()
            .WithTools<RetrieveContextTools>();

        await builder.Build().RunAsync();
        return 0;
    }
}
