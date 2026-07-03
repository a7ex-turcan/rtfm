using System.Text;
using Rtfm.Core.Configuration;
using Rtfm.Core.Indexing;

namespace Rtfm.Core.Generated;

/// <summary>Outcome of saving a generated document (Phase 19).</summary>
public sealed record SavedDocument(string Path, string Project, int ChunkCount, bool Replaced);

/// <summary>
/// Write-back for agent-generated knowledge (Phase 19): an analysis or report
/// produced with an LLM is persisted as a real <c>.md</c> file under the
/// generated-docs root and ingested through the normal pipeline — the file
/// stays the source of truth (§2.9's derived-index model holds; the folder can
/// always be re-indexed). Same title + project → same file → replaced, not
/// duplicated. A provenance line is prepended so both humans and LLMs see the
/// document is LLM-assisted — fresh generated content would otherwise outrank
/// stale-but-authoritative sources on recency (§2.13 B) without disclosure.
/// </summary>
public sealed class GeneratedDocumentStore(DocumentIngestor ingestor)
{
    /// <summary>Writes the markdown to disk and indexes it. Returns where it landed.</summary>
    public async Task<SavedDocument> SaveAsync(
        string title, string markdown, string project, string? author = null, CancellationToken cancellationToken = default)
    {
        var slug = Slugify(title);
        if (slug.Length == 0)
        {
            throw new ArgumentException("Title must contain at least one letter or digit.", nameof(title));
        }

        var directory = Path.Combine(RtfmEnvironment.ResolveGeneratedDirectory(), Slugify(project));
        Directory.CreateDirectory(directory);
        var path = Path.Combine(directory, $"{slug}.md");
        var replaced = File.Exists(path);

        var content = BuildFileContent(title, markdown, author ?? Environment.UserName, DateTimeOffset.Now);
        await File.WriteAllTextAsync(path, content, cancellationToken).ConfigureAwait(false);

        await ingestor.EnsureIndexAsync(cancellationToken).ConfigureAwait(false);
        var chunks = await ingestor.IngestFileAsync(path, project, DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
        await ingestor.RefreshAsync(cancellationToken).ConfigureAwait(false);

        return new SavedDocument(PathNormalizer.Normalize(path), project, chunks, replaced);
    }

    /// <summary>Title heading + provenance line + body (body's own leading H1 dropped if it repeats the title). Internal for tests.</summary>
    internal static string BuildFileContent(string title, string markdown, string author, DateTimeOffset savedAt)
    {
        var body = markdown.Trim();

        // Avoid a doubled title when the agent already leads with `# Title`.
        var firstLineEnd = body.IndexOf('\n');
        var firstLine = (firstLineEnd < 0 ? body : body[..firstLineEnd]).Trim();
        if (firstLine.StartsWith("# ", StringComparison.Ordinal)
            && string.Equals(firstLine[2..].Trim(), title.Trim(), StringComparison.OrdinalIgnoreCase))
        {
            body = firstLineEnd < 0 ? string.Empty : body[(firstLineEnd + 1)..].TrimStart();
        }

        var sb = new StringBuilder();
        sb.Append("# ").Append(title.Trim()).Append("\n\n");
        sb.Append("> LLM-assisted document, saved by ").Append(author)
            .Append(" on ").Append(savedAt.ToString("yyyy-MM-dd"))
            .Append(" via rtfm save_document. Verify against source docs where it matters.\n\n");
        sb.Append(body).Append('\n');
        return sb.ToString();
    }

    /// <summary>Filesystem-safe, stable slug: lower-cased, alnum runs joined by dashes, capped. Internal for tests.</summary>
    internal static string Slugify(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasDash = true; // suppress leading dash

        foreach (var c in text.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(c))
            {
                sb.Append(c);
                lastWasDash = false;
            }
            else if (!lastWasDash)
            {
                sb.Append('-');
                lastWasDash = true;
            }

            if (sb.Length >= 80)
            {
                break;
            }
        }

        return sb.ToString().TrimEnd('-');
    }
}
