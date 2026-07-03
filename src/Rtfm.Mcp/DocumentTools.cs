using System.ComponentModel;
using ModelContextProtocol.Server;
using Rtfm.Core.Configuration;
using Rtfm.Core.Generated;

namespace Rtfm.Mcp;

/// <summary>Phase 19 tool surface: write-back for agent-generated documents.</summary>
[McpServerToolType]
public static class DocumentTools
{
    [McpServerTool(Name = "save_document")]
    [Description("""
        Persist an LLM-produced document (analysis, feature review, gap list, decision record) into
        RTFM so future sessions can retrieve it like any other doc. The markdown is written to a real
        file under the generated-docs folder and indexed immediately: it appears in search_docs,
        list_sources, get_document, and contradiction detection runs against it.

        PRECONDITION: only call when the user directed you to save/remember the document (e.g.
        "remember this via rtfm") or confirmed a proposal to do so — never persist on your own
        initiative.

        Behavior to rely on:
        - Same title + project = same file, REPLACED — use the same title to update an analysis,
          a new title to add a separate one.
        - A provenance line ("LLM-assisted document, saved by … on …") is prepended automatically;
          don't add your own.
        - Write self-contained markdown with headings — sections become searchable chunks with
          breadcrumbs. Name concrete things (tables, endpoints, config keys) so lexical search works.
        """)]
    public static async Task<SaveDocumentResult> SaveDocument(
        GeneratedDocumentStore store,
        [Description("Document title, e.g. \"Feature analysis: MSP tenant switching\". Also the update key.")] string title,
        [Description("The full document as markdown (without repeating the title as an H1 — it is added).")] string markdown,
        [Description("Project to file it under. Omit to use the configured default.")] string? project = null,
        [Description("Who asked for / reviewed the document. Defaults to the local username.")] string? author = null,
        CancellationToken cancellationToken = default)
    {
        var scope = RtfmEnvironment.ResolveProjectScope(project) ?? "default";
        var saved = await store.SaveAsync(title, markdown, scope, author, cancellationToken).ConfigureAwait(false);

        return new SaveDocumentResult(
            Path: saved.Path,
            Project: saved.Project,
            ChunkCount: saved.ChunkCount,
            Replaced: saved.Replaced,
            Note: saved.Replaced
                ? "Replaced the previous version of this document."
                : "Saved as a new document; it is now searchable.");
    }
}

/// <summary>Structured tool output — serialized to JSON for the LLM by the SDK.</summary>
public sealed record SaveDocumentResult(string Path, string Project, int ChunkCount, bool Replaced, string Note);
