using System.ComponentModel;
using ModelContextProtocol.Server;
using Rtfm.Core.Configuration;
using Rtfm.Core.Search;

namespace Rtfm.Mcp;

/// <summary>
/// The MCP tool surface (§2.11). One tool for now: <c>search_docs</c>. The
/// method's parameters that are RTFM types (<see cref="DocumentSearch"/>,
/// <see cref="CancellationToken"/>) are injected from DI; the rest are the
/// tool's JSON arguments.
/// </summary>
[McpServerToolType]
public static class SearchDocsTool
{
    [McpServerTool(Name = "search_docs")]
    [Description("""
        Search the indexed project documentation and return the most relevant passages.
        Each result carries its project, source file, heading breadcrumb, last-modified date, and text.

        Use this whenever a question might be answered by the team's docs instead of guessing.

        Scope & provenance:
        - Results are limited to the configured project (RTFM_PROJECT) unless a `project` argument is given.
          Pass project="*" to search across ALL projects (e.g. to compare how two projects solved something).
        - Always attribute a fact to the source/project it came from.

        Recency & contradictions:
        - Within one project, when passages disagree prefer the one with the newer source_modified_at,
          and surface the disagreement to the user rather than silently choosing.
        - Across projects, differences are expected context — attribute each to its project; do NOT
          report them as contradictions.
        - The corpus is manually exported and can drift from the live wiki: treat a very old
          source_modified_at with suspicion, and mention the date when an answer rests on dated material.

        Follow-ups: each hit carries its `path` — pass it to get_document for the full page when the
        answer sprawls past the chunk, or to find_similar for related documents. list_sources shows
        what's indexed at all.
        """)]
    public static async Task<SearchDocsResult> SearchDocs(
        DocumentSearch search,
        [Description("Natural-language or keyword query.")] string query,
        [Description("Maximum number of passages to return (1-25).")] int top_k = 5,
        [Description("Project to search. Omit to use the configured default; pass \"*\" for all projects.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RtfmEnvironment.ResolveProjectScope(project);
        var hits = await search.SearchAsync(query, Math.Clamp(top_k, 1, 25), scope, cancellationToken).ConfigureAwait(false);

        return new SearchDocsResult(
            Query: query,
            ProjectScope: scope ?? "(all projects)",
            ResultCount: hits.Count,
            Results: hits.Select(h => new SearchDocsHit(
                Score: Math.Round(h.Score, 2),
                Project: h.Project,
                Source: Path.GetFileName(h.SourcePath),
                Path: h.SourcePath,
                HeadingPath: h.HeadingPath,
                SourceModifiedAt: h.SourceModifiedAt?.ToString("yyyy-MM-dd"),
                Text: h.Content)).ToList());
    }
}

/// <summary>Structured tool output — serialized to JSON for the LLM by the SDK.</summary>
public sealed record SearchDocsResult(
    string Query,
    string ProjectScope,
    int ResultCount,
    IReadOnlyList<SearchDocsHit> Results);

public sealed record SearchDocsHit(
    double Score,
    string Project,
    string Source,
    string Path,
    string HeadingPath,
    string? SourceModifiedAt,
    string Text);
