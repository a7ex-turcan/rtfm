using System.ComponentModel;
using ModelContextProtocol.Server;
using Rtfm.Core.Configuration;
using Rtfm.Core.Contradictions;
using Rtfm.Core.Notes;

namespace Rtfm.Mcp;

/// <summary>
/// Phase 12 tool surface: nominated doc-vs-doc disagreements (§2.13), plus the
/// Phase 22 lifecycle — dismiss (false positive) and resolve (confirmed, becomes
/// an override note). Both verdicts are human judgments: the descriptions
/// enforce explicit user confirmation, mirroring the add_note safety model.
/// </summary>
[McpServerToolType]
public static class ContradictionTools
{
    [McpServerTool(Name = "list_contradictions")]
    [Description("""
        List nominated contradictions: pairs of semantically-similar passages from DIFFERENT documents
        in the SAME project whose content may disagree (e.g. an older page says the default role is
        "admin", a newer one says "super-admin"). Open nominations only by default, newest first.

        Each pair carries a `kind`:
        - "likely-supersession" — the sides' dates are far apart (30+ days): the newer doc probably
          superseded the older. Confirm the newer side is right, prefer it, and mention that the older
          doc looks stale.
        - "contradiction" — close dates: two live documents genuinely disagree. Read both sides
          (get_document on the paths) and surface the conflict to the user rather than silently choosing.

        These are NOMINATIONS from a dumb heuristic, not verdicts. Lifecycle (both require the user's
        explicit confirmation in this conversation):
        - Not a real conflict → dismiss_contradiction(id). Stays dismissed across re-indexing.
        - Real and settled → resolve_contradiction(id, correction) — records the user-confirmed answer
          as an override note and closes the pair.

        Cross-project differences are never nominated — different projects are expected to differ.
        """)]
    public static async Task<ListContradictionsResult> ListContradictions(
        ContradictionDetector detector,
        [Description("Project to inspect. Omit to use the configured default; pass \"*\" for all projects.")] string? project = null,
        [Description("Maximum number of pairs to return (1-50).")] int top_k = 20,
        [Description("Also include dismissed/resolved pairs (history). Default: open only.")] bool include_closed = false,
        CancellationToken cancellationToken = default)
    {
        var scope = RtfmEnvironment.ResolveProjectScope(project);
        var pairs = await detector.ListAsync(scope, Math.Clamp(top_k, 1, 50), include_closed, cancellationToken).ConfigureAwait(false);

        return new ListContradictionsResult(
            ProjectScope: scope ?? "(all projects)",
            PairCount: pairs.Count,
            Pairs: pairs.Select(p => new ContradictionEntry(
                Id: p.Id,
                Project: p.Project,
                Kind: p.Kind,
                Status: p.Status,
                Similarity: p.Similarity,
                DetectedAt: p.DetectedAt.ToString("yyyy-MM-dd"),
                Newer: Entry(p.A),
                Older: Entry(p.B))).ToList());
    }

    [McpServerTool(Name = "dismiss_contradiction")]
    [Description("""
        Mark a nominated contradiction as not a real conflict (false positive: template overlap, scoped
        drafts, passages that merely discuss the same topic). Dismissed pairs disappear from open
        nominations and STAY dismissed across re-indexing.

        STRICT PRECONDITION — human in the loop: only call this after the user has EXPLICITLY agreed,
        in this conversation, that this specific pair is not a real conflict. Never dismiss on your own
        judgment alone — propose, get a yes, then call.
        """)]
    public static async Task<PairStatusResult> DismissContradiction(
        ContradictionDetector detector,
        [Description("The pair id (from list_contradictions).")] string id,
        CancellationToken cancellationToken = default)
    {
        var pair = await detector.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (pair is null)
        {
            return new PairStatusResult(false, id, null, null, $"No contradiction pair with id '{id}'. Use list_contradictions to see current nominations.");
        }

        await detector.SetStatusAsync(id, ContradictionPair.StatusDismissed, cancellationToken: cancellationToken).ConfigureAwait(false);
        return new PairStatusResult(true, id, ContradictionPair.StatusDismissed, null,
            "Pair dismissed. It will not be re-nominated when either document is re-indexed.");
    }

    [McpServerTool(Name = "resolve_contradiction")]
    [Description("""
        Close a nominated contradiction with a user-confirmed answer: records the correction as an
        override note (anchored to the OLDER/stale document, so its future search hits carry the
        correction) and marks the pair resolved. Resolved pairs stay closed across re-indexing.

        STRICT PRECONDITION — human in the loop: only call this after the user has EXPLICITLY
        confirmed, in this conversation, both which side is right AND the exact correction text.
        Propose the wording, get a yes, then call. Write the correction as a standalone fact
        (e.g. "MSP access is governed by the msp-operator role since June 2026; the HLD's
        msp_enabled flag is obsolete.").
        """)]
    public static async Task<PairStatusResult> ResolveContradiction(
        ContradictionDetector detector,
        NotesStore notes,
        [Description("The pair id (from list_contradictions).")] string id,
        [Description("The user-confirmed correction, as a standalone, self-explanatory statement.")] string correction,
        [Description("Who confirmed the resolution. Defaults to the local username.")] string? author = null,
        CancellationToken cancellationToken = default)
    {
        var pair = await detector.GetAsync(id, cancellationToken).ConfigureAwait(false);
        if (pair is null)
        {
            return new PairStatusResult(false, id, null, null, $"No contradiction pair with id '{id}'. Use list_contradictions to see current nominations.");
        }

        // Anchor to the older (likely stale) side: that document's future hits
        // must carry the correction. The note also matches searches directly.
        var note = await notes.AddAsync(correction, pair.Project, pair.B.Path, author, cancellationToken).ConfigureAwait(false);
        await detector.SetStatusAsync(id, ContradictionPair.StatusResolved, note.Id, cancellationToken).ConfigureAwait(false);

        return new PairStatusResult(true, id, ContradictionPair.StatusResolved, note.Id,
            $"Pair resolved. Correction saved as override note {note.Id}, anchored to {note.TargetPath}.");
    }

    private static ContradictionSideEntry Entry(ContradictionSide side) => new(
        Path: side.Path,
        Heading: side.Heading,
        SourceModifiedAt: side.ModifiedAt?.ToString("yyyy-MM-dd"),
        Excerpt: side.Excerpt);
}

/// <summary>Structured tool outputs — serialized to JSON for the LLM by the SDK.</summary>
public sealed record ListContradictionsResult(string ProjectScope, int PairCount, IReadOnlyList<ContradictionEntry> Pairs);

public sealed record ContradictionEntry(
    string Id, string Project, string Kind, string Status, double Similarity, string DetectedAt,
    ContradictionSideEntry Newer, ContradictionSideEntry Older);

public sealed record ContradictionSideEntry(string Path, string Heading, string? SourceModifiedAt, string Excerpt);

public sealed record PairStatusResult(bool Found, string Id, string? Status, string? NoteId, string Message);
