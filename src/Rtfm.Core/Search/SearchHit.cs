namespace Rtfm.Core.Search;

/// <summary>One search result: a chunk plus its BM25 score and recency (§2.13 B).</summary>
public sealed record SearchHit(
    double Score,
    string SourcePath,
    string HeadingPath,
    string? Title,
    string Content,
    DateTimeOffset? SourceModifiedAt);
