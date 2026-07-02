// Rtfm.Mcp — stdio MCP server exposing search_docs (§2.11).
//
// IMPORTANT (CLAUDE.md §2.2): stdout carries the MCP protocol. Every diagnostic
// MUST go to stderr — so we clear the default logger and add a console logger
// pinned to stderr for all levels. One stray stdout write breaks the transport.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rtfm.Core.OpenSearch;
using Rtfm.Core.Search;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// Shared RTFM services (explicit factories avoid constructor ambiguity).
builder.Services.AddSingleton(_ => new OpenSearchGateway());
builder.Services.AddSingleton(sp => new DocumentSearch(sp.GetRequiredService<OpenSearchGateway>()));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
