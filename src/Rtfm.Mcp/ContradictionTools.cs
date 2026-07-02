using System.ComponentModel;
using ModelContextProtocol.Server;
using Rtfm.Core.Configuration;
using Rtfm.Core.Contradictions;

namespace Rtfm.Mcp;

/// <summary>Phase 12 tool surface: nominated doc-vs-doc disagreements (§2.13).</summary>
[McpServerToolType]
public static class ContradictionTools
{
    [McpServerTool(Name = "list_contradictions")]
    [Description("""
        List nominated contradictions: pairs of semantically-similar passages from DIFFERENT documents
        in the SAME project whose content may disagree (e.g. an older page says the default role is
        "admin", a newer one says "super-admin"). Newest nominations first.

        These are NOMINATIONS from a dumb heuristic (high similarity + different source dates + not
        identical text) — not verdicts. For any pair that matters to the user's question:
        1. Read both sides (each entry carries excerpts; use get_document on the paths for full context).
        2. Decide whether they truly disagree or just overlap.
        3. If they disagree: prefer the NEWER side (side "a") as likely authoritative, but SURFACE the
           conflict to the user rather than silently choosing — newer-is-truth is a heuristic, not a law.

        Cross-project differences are never nominated — different projects are expected to differ.
        """)]
    public static async Task<ListContradictionsResult> ListContradictions(
        ContradictionDetector detector,
        [Description("Project to inspect. Omit to use the configured default; pass \"*\" for all projects.")] string? project = null,
        [Description("Maximum number of pairs to return (1-50).")] int top_k = 20,
        CancellationToken cancellationToken = default)
    {
        var scope = RtfmEnvironment.ResolveProjectScope(project);
        var pairs = await detector.ListAsync(scope, Math.Clamp(top_k, 1, 50), cancellationToken).ConfigureAwait(false);

        return new ListContradictionsResult(
            ProjectScope: scope ?? "(all projects)",
            PairCount: pairs.Count,
            Pairs: pairs.Select(p => new ContradictionEntry(
                Project: p.Project,
                Similarity: p.Similarity,
                DetectedAt: p.DetectedAt.ToString("yyyy-MM-dd"),
                Newer: Entry(p.A),
                Older: Entry(p.B))).ToList());
    }

    private static ContradictionSideEntry Entry(ContradictionSide side) => new(
        Path: side.Path,
        Heading: side.Heading,
        SourceModifiedAt: side.ModifiedAt?.ToString("yyyy-MM-dd"),
        Excerpt: side.Excerpt);
}

/// <summary>Structured tool outputs — serialized to JSON for the LLM by the SDK.</summary>
public sealed record ListContradictionsResult(string ProjectScope, int PairCount, IReadOnlyList<ContradictionEntry> Pairs);

public sealed record ContradictionEntry(string Project, double Similarity, string DetectedAt, ContradictionSideEntry Newer, ContradictionSideEntry Older);

public sealed record ContradictionSideEntry(string Path, string Heading, string? SourceModifiedAt, string Excerpt);
