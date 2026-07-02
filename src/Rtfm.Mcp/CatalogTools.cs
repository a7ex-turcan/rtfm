using System.ComponentModel;
using ModelContextProtocol.Server;
using Rtfm.Core.Configuration;
using Rtfm.Core.Search;

namespace Rtfm.Mcp;

/// <summary>
/// The Phase 8 tool surface: corpus awareness (<c>list_sources</c>), full-page
/// reads (<c>get_document</c>), and related-document discovery
/// (<c>find_similar</c>). Same DI + scoping rules as <c>search_docs</c>.
/// </summary>
[McpServerToolType]
public static class CatalogTools
{
    [McpServerTool(Name = "list_sources")]
    [Description("""
        List every indexed documentation source: path, title, project, last-modified date, chunk count.

        Use this for corpus awareness — to see what documentation exists at all, to check whether a
        question is even answerable from the docs (a weak search hit against a corpus that plainly
        doesn't cover the topic means "the docs don't say", not "keep re-querying"), or to find the
        exact path/title of a document to pass to get_document / find_similar.

        Scope follows the same rules as search_docs: RTFM_PROJECT by default, `project` argument to
        override, "*" for all projects.
        """)]
    public static async Task<ListSourcesResult> ListSources(
        DocumentCatalog catalog,
        [Description("Project to list. Omit to use the configured default; pass \"*\" for all projects.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RtfmEnvironment.ResolveProjectScope(project);
        var sources = await catalog.ListSourcesAsync(scope, cancellationToken).ConfigureAwait(false);

        return new ListSourcesResult(
            ProjectScope: scope ?? "(all projects)",
            SourceCount: sources.Count,
            Sources: sources.Select(s => new SourceEntry(
                Path: s.Path,
                Title: s.Title,
                Project: s.Project,
                SourceModifiedAt: s.SourceModifiedAt?.ToString("yyyy-MM-dd"),
                ChunkCount: s.ChunkCount)).ToList());
    }

    [McpServerTool(Name = "get_document")]
    [Description("""
        Fetch one full document as markdown, reassembled from its indexed chunks in order.

        Use this when a search_docs hit is clearly the right document but the answer sprawls past
        the returned chunk — tables that continue, multi-section procedures, surrounding context.
        Pass the hit's `path` (preferred) or its `source` filename.

        Note: this is the *converted* form of the document (the original may be a Confluence MHTML
        export). Sections that were split with overlap can repeat a few lines at the seams.
        """)]
    public static async Task<GetDocumentResult> GetDocument(
        DocumentCatalog catalog,
        [Description("Document path as returned by search_docs/list_sources, or just the filename.")] string path,
        [Description("Project to search in when resolving a bare filename. Omit for the configured default; \"*\" for all.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RtfmEnvironment.ResolveProjectScope(project);
        var document = await catalog.GetDocumentAsync(path, scope, cancellationToken).ConfigureAwait(false);

        if (document is null)
        {
            return new GetDocumentResult(Found: false, Path: path, Title: null, Project: null,
                SourceModifiedAt: null, ChunkCount: 0,
                Markdown: $"No indexed document matches '{path}' in scope '{scope ?? "(all projects)"}'. Use list_sources to see what exists.");
        }

        return new GetDocumentResult(
            Found: true,
            Path: document.Path,
            Title: document.Title,
            Project: document.Project,
            SourceModifiedAt: document.SourceModifiedAt?.ToString("yyyy-MM-dd"),
            ChunkCount: document.ChunkCount,
            Markdown: document.Markdown);
    }

    [McpServerTool(Name = "find_similar")]
    [Description("""
        Find the documents semantically closest to a given one (the document itself is excluded).
        Ranked by the best-matching chunk; each result names that chunk's heading as a hint to *why*
        it is related.

        Use this to answer "what else discusses this topic?", to widen research around a relevant
        document, or to locate a newer/other document that may supersede or contradict this one
        (compare sourceModifiedAt, and apply the same recency rules as search_docs).
        """)]
    public static async Task<FindSimilarResult> FindSimilar(
        DocumentCatalog catalog,
        [Description("Document path as returned by search_docs/list_sources, or just the filename.")] string path,
        [Description("Maximum number of similar documents to return (1-25).")] int top_k = 5,
        [Description("Project to search within. Omit for the configured default; \"*\" for all projects.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RtfmEnvironment.ResolveProjectScope(project);
        var result = await catalog.FindSimilarAsync(path, Math.Clamp(top_k, 1, 25), scope, cancellationToken).ConfigureAwait(false);

        if (result is null)
        {
            return new FindSimilarResult(Found: false, VectorsAvailable: false, Path: path, SimilarCount: 0, Similar: [],
                Note: $"No indexed document matches '{path}' in scope '{scope ?? "(all projects)"}'. Use list_sources to see what exists.");
        }

        if (!result.VectorsAvailable)
        {
            return new FindSimilarResult(Found: true, VectorsAvailable: false, Path: path, SimilarCount: 0, Similar: [],
                Note: "This document was indexed without embeddings (lexical-only run); re-run 'rtfm index' with the model available to enable similarity.");
        }

        return new FindSimilarResult(
            Found: true,
            VectorsAvailable: true,
            Path: path,
            SimilarCount: result.Similar.Count,
            Similar: result.Similar.Select(s => new SimilarEntry(
                Path: s.Path,
                Title: s.Title,
                Project: s.Project,
                Score: s.Score,
                BestMatchingHeading: s.BestMatchingHeading,
                SourceModifiedAt: s.SourceModifiedAt?.ToString("yyyy-MM-dd"))).ToList(),
            Note: null);
    }
}

/// <summary>Structured tool outputs — serialized to JSON for the LLM by the SDK.</summary>
public sealed record ListSourcesResult(string ProjectScope, int SourceCount, IReadOnlyList<SourceEntry> Sources);

public sealed record SourceEntry(string Path, string? Title, string Project, string? SourceModifiedAt, long ChunkCount);

public sealed record GetDocumentResult(bool Found, string Path, string? Title, string? Project, string? SourceModifiedAt, int ChunkCount, string Markdown);

public sealed record FindSimilarResult(bool Found, bool VectorsAvailable, string Path, int SimilarCount, IReadOnlyList<SimilarEntry> Similar, string? Note);

public sealed record SimilarEntry(string Path, string? Title, string Project, double Score, string BestMatchingHeading, string? SourceModifiedAt);
