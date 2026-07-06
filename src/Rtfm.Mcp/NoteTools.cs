using System.ComponentModel;
using ModelContextProtocol.Server;
using Rtfm.Core.Configuration;
using Rtfm.Core.Notes;

namespace Rtfm.Mcp;

/// <summary>
/// Phase 13 tool surface (§2.13 C): override notes — user-confirmed corrections
/// that survive re-indexing. Human-in-the-loop is enforced by the tool
/// descriptions: the agent proposes, the human confirms, only then does the
/// agent write.
/// </summary>
[McpServerToolType]
public static class NoteTools
{
    [McpServerTool(Name = "add_note")]
    [Description("""
        Record a user-confirmed correction as an override note. Notes live outside the document
        index, survive every re-index, and appear in search_docs results as attributed overrides
        (origin "note") — they never replace or hide the original text.

        STRICT PRECONDITION — human in the loop: only call this after the user has EXPLICITLY
        confirmed, in this conversation, both that the docs are wrong/outdated AND the exact
        correction text. Never call it on your own inference. Propose the wording, get a yes,
        then call.

        Write the note text as a standalone fact with enough context to be understood alone
        (e.g. "The default user-role for new accounts is super-admin since May 2026; the RBAC
        page still says admin and is outdated."). Pass `path` (from search hits) to anchor the
        note to the document it corrects — anchored notes are shown alongside that document's
        future search hits. OMIT `path` for project-level decisions that live above any single
        document (architecture choices, cross-cutting conventions): unanchored notes are
        first-class and still match searches — do not pick an arbitrary anchor.

        Retry-safe: the note id is derived from project + text + path, so re-calling with
        identical arguments (e.g. after a timeout) updates the same note instead of duplicating.
        """)]
    public static async Task<AddNoteResult> AddNote(
        NotesStore store,
        [Description("The correction, as a standalone, self-explanatory statement (user-confirmed wording).")] string text,
        [Description("Project the correction belongs to. Omit to use the configured default.")] string? project = null,
        [Description("Path of the document this corrects (from search_docs/list_sources), if any.")] string? path = null,
        [Description("Who confirmed the correction. Defaults to the local username.")] string? author = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RtfmEnvironment.ResolveProjectScope(project) ?? "default";
        var note = await store.AddAsync(text, scope, path, author, cancellationToken).ConfigureAwait(false);

        return new AddNoteResult(note.Id, note.Project, note.Text, note.TargetPath, note.Author, note.CreatedAt.ToString("yyyy-MM-dd"));
    }

    [McpServerTool(Name = "list_notes")]
    [Description("""
        List override notes (user-confirmed corrections), newest first. Use to check what has
        already been corrected before answering from docs, or to review notes with the user.
        """)]
    public static async Task<ListNotesResult> ListNotes(
        NotesStore store,
        [Description("Project to list. Omit for the configured default; pass \"*\" for all projects.")] string? project = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RtfmEnvironment.ResolveProjectScope(project);
        var notes = await store.ListAsync(scope, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ListNotesResult(
            ProjectScope: scope ?? "(all projects)",
            NoteCount: notes.Count,
            Notes: notes.Select(n => new NoteEntry(n.Id, n.Project, n.Text, n.TargetPath, n.Author, n.CreatedAt.ToString("yyyy-MM-dd"))).ToList());
    }

    [McpServerTool(Name = "remove_note")]
    [Description("""
        Delete an override note by id. STRICT PRECONDITION: only call when the user explicitly
        asked to remove this specific note (e.g. after the source docs were fixed).
        """)]
    public static async Task<RemoveNoteResult> RemoveNote(
        NotesStore store,
        [Description("The note id (from list_notes / add_note).")] string id,
        CancellationToken cancellationToken = default)
    {
        var removed = await store.RemoveAsync(id, cancellationToken).ConfigureAwait(false);
        return new RemoveNoteResult(removed, removed ? $"Note {id} removed." : $"No note with id '{id}'.");
    }
}

/// <summary>Structured tool outputs — serialized to JSON for the LLM by the SDK.</summary>
public sealed record AddNoteResult(string Id, string Project, string Text, string? Path, string Author, string CreatedAt);

public sealed record ListNotesResult(string ProjectScope, int NoteCount, IReadOnlyList<NoteEntry> Notes);

public sealed record NoteEntry(string Id, string Project, string Text, string? Path, string Author, string CreatedAt);

public sealed record RemoveNoteResult(bool Removed, string Message);
