using CCStash.Core;
using CCStash.Core.Config;
using CCStash.Core.Storage;
using CCStash.Mcp;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace CCStash.Verbs;

/// <summary>Handles the <c>mcp</c> verb: runs the MCP server for the current project over stdio (default) or HTTP (<c>--http</c>).</summary>
internal static class McpVerb
{
    /// <summary>Default port for the HTTP transport when <c>--http</c> is passed without <c>--port</c>.</summary>
    internal const int DefaultHttpPort = 6733;

    /// <summary>Start the MCP server, exposing retrieve_context/list_stashes over stdio or HTTP.</summary>
    public static async Task<int> RunAsync(string cwd, string[] args)
    {
        var cfg = CCStashConfig.Load(CCStashPaths.ConfigPath);
        var embedder = await Composition.BuildEmbedderAsync(cfg);
        var store = Composition.BuildStore(cwd, cfg);
        await store.InitializeAsync(embedder.Dimension, embedder.ModelId);
        var retrieval = new RetrievalService(embedder, store);

        if (WantsHttp(args))
        {
            var port = ResolvePort(args);
            var webBuilder = WebApplication.CreateBuilder();

            // No JSON-RPC transport shares stdout in HTTP mode, but logging still goes to stderr
            // only, matching the stdio path's convention.
            webBuilder.Logging.ClearProviders();
            webBuilder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);

            webBuilder.WebHost.UseUrls($"http://localhost:{port}");

            webBuilder.Services.AddSingleton<IVectorStore>(store);
            webBuilder.Services.AddSingleton<IRetrievalService>(retrieval);
            webBuilder.Services.AddSingleton(new McpToolContext(cfg.ProjectWide));

            webBuilder.Services
                .AddMcpServer()
                .WithHttpTransport()
                .WithTools<RetrieveContextTools>();

            var app = webBuilder.Build();
            app.MapMcp("/mcp");

            app.Logger.LogInformation("CCStash MCP server listening on http://localhost:{Port}/mcp", port);
            await app.RunAsync();
            return 0;
        }

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

    /// <summary>True when <c>--http</c> is present, selecting the HTTP transport over the default stdio one.</summary>
    internal static bool WantsHttp(string[] args) => args.Contains("--http");

    /// <summary>Resolves the HTTP listen port from <c>--port &lt;n&gt;</c>, falling back to <see cref="DefaultHttpPort"/>.</summary>
    internal static int ResolvePort(string[] args)
    {
        var i = Array.IndexOf(args, "--port");
        if (i >= 0 && i + 1 < args.Length && int.TryParse(args[i + 1], out var port))
        {
            return port;
        }

        return DefaultHttpPort;
    }
}
