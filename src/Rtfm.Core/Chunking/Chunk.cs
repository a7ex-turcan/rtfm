namespace Rtfm.Core.Chunking;

/// <summary>Document-level facts every chunk of a document carries (§2.7, §2.13, §2.14).</summary>
public sealed record ChunkMetadata(
    string SourcePath,
    string? DocumentTitle,
    DateTimeOffset? SourceModifiedAt,
    string Project = "default");

/// <summary>
/// One indexable unit: a section's body plus the heading breadcrumb that locates
/// it (e.g. <c>RBAC &gt; Functional Requirements</c>). The breadcrumb is stored
/// separately (for display and as a keyword field) and also prepended to the
/// indexed text (§2.7) so both keyword and vector search see the context.
/// </summary>
public sealed record Chunk(
    int Ordinal,
    string SourcePath,
    string HeadingPath,
    string Text,
    string? DocumentTitle,
    DateTimeOffset? SourceModifiedAt,
    string Project = "default")
{
    /// <summary>What actually gets indexed / embedded: breadcrumb + body.</summary>
    public string ContentWithBreadcrumb =>
        Text.Length == 0 ? HeadingPath : $"{HeadingPath}\n\n{Text}";
}
