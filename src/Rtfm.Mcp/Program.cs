// Rtfm.Mcp — stdio MCP server exposing search_docs (§2.11).
//
// IMPORTANT (CLAUDE.md §2.2): stdout carries the MCP protocol. Every diagnostic
// MUST go to stderr — so we clear the default logger and add a console logger
// pinned to stderr for all levels. One stray stdout write breaks the transport.
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Rtfm.Core.Embeddings;
using Rtfm.Core.OpenSearch;
using Rtfm.Core.Search;

var builder = Host.CreateApplicationBuilder(args);

builder.Logging.ClearProviders();
builder.Logging.AddConsole(options => options.LogToStandardErrorThreshold = LogLevel.Trace);

// Shared RTFM services (explicit factories avoid constructor ambiguity).
// The embedder is lazy: nothing loads until the first search_docs call, so the
// MCP handshake stays instant. If the model can't be loaded (e.g. offline and
// not yet cached), DocumentSearch degrades to Tier 1 BM25 and logs to stderr.
builder.Services.AddSingleton(_ => new OpenSearchGateway());
builder.Services.AddSingleton<ITextEmbedder>(_ => new LocalEmbedder(new EmbeddingModelStore(log: Console.Error.WriteLine)));
builder.Services.AddSingleton<IReranker>(_ => new CrossEncoder(EmbeddingModelStore.ForReranker(log: Console.Error.WriteLine)));
builder.Services.AddSingleton(sp => new Rtfm.Core.Notes.NotesStore(
    sp.GetRequiredService<OpenSearchGateway>(),
    sp.GetRequiredService<ITextEmbedder>()));
builder.Services.AddSingleton(sp => new DocumentSearch(
    sp.GetRequiredService<OpenSearchGateway>(),
    sp.GetRequiredService<ITextEmbedder>(),
    Console.Error.WriteLine,
    sp.GetRequiredService<IReranker>(),
    sp.GetRequiredService<Rtfm.Core.Notes.NotesStore>()));
builder.Services.AddSingleton(sp => new DocumentCatalog(sp.GetRequiredService<OpenSearchGateway>()));
builder.Services.AddSingleton(sp => new Rtfm.Core.Contradictions.ContradictionDetector(sp.GetRequiredService<OpenSearchGateway>()));

// Phase 19 write-back: save_document ingests through the same pipeline the CLI
// uses (embedder + contradiction detection included).
builder.Services.AddSingleton(sp => new Rtfm.Core.Indexing.DocumentIngestor(
    new Rtfm.Core.Indexing.DocumentIndexer(sp.GetRequiredService<OpenSearchGateway>()),
    sp.GetRequiredService<ITextEmbedder>(),
    sp.GetRequiredService<Rtfm.Core.Contradictions.ContradictionDetector>()));
builder.Services.AddSingleton(sp => new Rtfm.Core.Generated.GeneratedDocumentStore(
    sp.GetRequiredService<Rtfm.Core.Indexing.DocumentIngestor>()));

builder.Services
    .AddMcpServer()
    .WithStdioServerTransport()
    .WithToolsFromAssembly();

await builder.Build().RunAsync();
