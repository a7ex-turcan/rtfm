namespace Rtfm.Core.Search;

/// <summary>One search result: a chunk plus its BM25 score, project, and recency (§2.13 B, §2.14).</summary>
public sealed record SearchHit(
    double Score,
    string Project,
    string SourcePath,
    string HeadingPath,
    string? Title,
    string Content,
    DateTimeOffset? SourceModifiedAt);
