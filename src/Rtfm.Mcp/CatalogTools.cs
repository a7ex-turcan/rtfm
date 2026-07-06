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
    [McpServerTool(Name = "list_projects")]
    [Description("""
        List every indexed project: name, document/chunk counts, newest source date, last index time,
        and vector coverage (fraction of chunks that are semantically searchable).

        Use this to discover what project names exist before scoping search_docs / list_sources with
        `project`, or to check which project a corpus lives in. Cheap — one aggregation, tiny response.
        """)]
    public static async Task<ListProjectsResult> ListProjects(
        StatusService status,
        CancellationToken cancellationToken = default)
    {
        var projects = await status.GetProjectStatusesAsync(null, cancellationToken).ConfigureAwait(false);
        return new ListProjectsResult(projects.Count, projects.Select(ToProjectEntry).ToList());
    }

    [McpServerTool(Name = "list_sources")]
    [Description("""
        List indexed documentation sources: path, title, project, last-modified date, chunk count.

        Use this for corpus awareness — to see what documentation exists at all, to check whether a
        question is even answerable from the docs (a weak search hit against a corpus that plainly
        doesn't cover the topic means "the docs don't say", not "keep re-querying"), or to find the
        exact path/title of a document to pass to get_document / find_similar.

        Scope follows the same rules as search_docs: RTFM_PROJECT by default, `project` argument to
        override, "*" for all projects. An UNSCOPED call spanning several projects returns a compact
        per-project summary instead of every document — pass a specific `project` (or full=true, if
        you really need the cross-project dump) for the document listing.
        """)]
    public static async Task<ListSourcesResult> ListSources(
        DocumentCatalog catalog,
        StatusService status,
        [Description("Project to list. Omit to use the configured default; pass \"*\" for all projects.")] string? project = null,
        [Description("Force the full per-document listing even when unscoped across many projects.")] bool full = false,
        CancellationToken cancellationToken = default)
    {
        var scope = RtfmEnvironment.ResolveProjectScope(project);

        // Unscoped across several projects: a full dump is mostly other
        // projects' noise (observed: 184 sources, ~170 irrelevant). Summarize
        // per project instead; one known project falls through to the listing.
        if (scope is null && !full)
        {
            var projects = await status.GetProjectStatusesAsync(null, cancellationToken).ConfigureAwait(false);
            if (projects.Count > 1)
            {
                return new ListSourcesResult(
                    ProjectScope: "(all projects)",
                    SourceCount: 0,
                    Sources: [],
                    Projects: projects.Select(ToProjectEntry).ToList(),
                    Note: $"Unscoped call across {projects.Count} projects ({projects.Sum(p => p.DocCount)} documents) — "
                        + "showing the per-project summary. Pass `project` to list one project's documents, or full=true for everything.");
            }
        }

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

    private static ProjectEntry ToProjectEntry(ProjectStatus p) => new(
        Project: p.Project,
        DocCount: p.DocCount,
        ChunkCount: p.ChunkCount,
        NewestSourceModifiedAt: p.NewestSourceModified?.ToString("yyyy-MM-dd"),
        LastIndexedAt: p.LastIndexedAt?.ToString("yyyy-MM-dd"),
        VectorCoverage: Math.Round(p.VectorCoverage, 2));

    [McpServerTool(Name = "get_document")]
    [Description("""
        Fetch one document as markdown, reassembled from its indexed chunks in order.

        Use this when a search_docs hit is clearly the right document but the answer sprawls past
        the returned chunk — tables that continue, multi-section procedures, surrounding context.
        Pass the hit's `path` (preferred) or its `source` filename.

        For long documents, prefer a partial fetch: pass the hit's `ordinal` as `around_ordinal`
        (with `radius` chunks of context either side) to get just the relevant section instead of
        the whole page. Omit `around_ordinal` for the full document.

        Note: this is the *converted* form of the document (the original may be a Confluence MHTML
        export). Sections that were split with overlap can repeat a few lines at the seams.
        """)]
    public static async Task<GetDocumentResult> GetDocument(
        DocumentCatalog catalog,
        [Description("Document path as returned by search_docs/list_sources, or just the filename.")] string path,
        [Description("Project to search in when resolving a bare filename. Omit for the configured default; \"*\" for all.")] string? project = null,
        [Description("Chunk ordinal (from a search_docs hit) to center a partial fetch on. Omit for the whole document.")] int? around_ordinal = null,
        [Description("Chunks of context either side of around_ordinal (0-10).")] int radius = 2,
        CancellationToken cancellationToken = default)
    {
        var scope = RtfmEnvironment.ResolveProjectScope(project);
        var document = await catalog.GetDocumentAsync(path, scope, around_ordinal, Math.Clamp(radius, 0, 10), cancellationToken).ConfigureAwait(false);

        if (document is null)
        {
            var hint = around_ordinal is null
                ? "Use list_sources to see what exists."
                : $"The document may have fewer chunks than ordinal {around_ordinal} — retry without around_ordinal, or use list_sources.";
            return new GetDocumentResult(Found: false, Path: path, Title: null, Project: null,
                SourceModifiedAt: null, ChunkCount: 0, Partial: false,
                Markdown: $"No indexed chunks match '{path}' in scope '{scope ?? "(all projects)"}'. {hint}");
        }

        return new GetDocumentResult(
            Found: true,
            Path: document.Path,
            Title: document.Title,
            Project: document.Project,
            SourceModifiedAt: document.SourceModifiedAt?.ToString("yyyy-MM-dd"),
            ChunkCount: document.ChunkCount,
            Partial: document.Partial,
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
public sealed record ListProjectsResult(int ProjectCount, IReadOnlyList<ProjectEntry> Projects);

public sealed record ProjectEntry(
    string Project, long DocCount, long ChunkCount, string? NewestSourceModifiedAt, string? LastIndexedAt, double VectorCoverage);

public sealed record ListSourcesResult(
    string ProjectScope,
    int SourceCount,
    IReadOnlyList<SourceEntry> Sources,
    IReadOnlyList<ProjectEntry>? Projects = null,
    string? Note = null);

public sealed record SourceEntry(string Path, string? Title, string Project, string? SourceModifiedAt, long ChunkCount);

public sealed record GetDocumentResult(bool Found, string Path, string? Title, string? Project, string? SourceModifiedAt, int ChunkCount, bool Partial, string Markdown);

public sealed record FindSimilarResult(bool Found, bool VectorsAvailable, string Path, int SimilarCount, IReadOnlyList<SimilarEntry> Similar, string? Note);

public sealed record SimilarEntry(string Path, string? Title, string Project, double Score, string BestMatchingHeading, string? SourceModifiedAt);
