using Rtfm.Core.Embeddings;
using Rtfm.Core.Search;

namespace Rtfm.Core.Tests.Embeddings;

/// <summary>
/// Pure logic only — running the real cross-encoder needs an ~87 MB download,
/// exercised by the live Phase 11 validation instead.
/// </summary>
public class CrossEncoderTests
{
    private const int Cls = 101, Sep = 102;

    [Fact]
    public void BuildPair_produces_cls_a_sep_b_sep_with_segment_ids()
    {
        // query: [CLS] 7 8 [SEP], passage: [CLS] 9 [SEP]
        var (ids, typeIds) = CrossEncoder.BuildPair([Cls, 7, 8, Sep], [Cls, 9, Sep], Sep, maxTokens: 512);

        Assert.Equal([Cls, 7, 8, Sep, 9, Sep], ids);
        Assert.Equal([0, 0, 0, 0, 1, 1], typeIds);
    }

    [Fact]
    public void BuildPair_truncates_the_passage_side_and_keeps_the_final_sep()
    {
        var query = new[] { Cls, 7, Sep };
        var passage = new[] { Cls }.Concat(Enumerable.Repeat(9, 20)).Append(Sep).ToArray();

        var (ids, typeIds) = CrossEncoder.BuildPair(query, passage, Sep, maxTokens: 10);

        Assert.Equal(10, ids.Length);
        Assert.Equal(Sep, ids[^1]);
        Assert.Equal([Cls, 7, Sep], ids[..3]); // query side untouched
        Assert.Equal(0, typeIds[2]);
        Assert.All(typeIds[3..], t => Assert.Equal(1, t));
    }

    [Fact]
    public void Rerank_reorders_by_score_and_squashes_to_unit_range()
    {
        var hits = new List<SearchHit>
        {
            new(0.9, "p", "a", "H1", null, "first by fusion", null),
            new(0.5, "p", "b", "H2", null, "second by fusion", null),
            new(0.1, "p", "c", "H3", null, "third by fusion", null),
        };

        // Cross-encoder disagrees: the fused #3 is actually the best answer.
        var reranked = DocumentSearch.Rerank(hits, [-2.0f, 0.0f, 4.0f], topK: 2);

        Assert.Equal(2, reranked.Count);
        Assert.Equal("c", reranked[0].SourcePath);
        Assert.Equal("b", reranked[1].SourcePath);
        Assert.All(reranked, h => Assert.InRange(h.Score, 0.0, 1.0));
        Assert.True(reranked[0].Score > 0.95); // sigmoid(4) ≈ 0.982
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(4.0, 0.982)]
    [InlineData(-4.0, 0.018)]
    public void Sigmoid_squashes_logits(double logit, double expected)
        => Assert.Equal(expected, DocumentSearch.Sigmoid(logit), precision: 3);

    [Fact]
    public void Short_chunks_pass_through_as_single_windows()
    {
        var hits = new List<SearchHit>
        {
            new(1, "p", "a", "H", null, "H\n\nshort body", null),
        };

        var (windows, owner) = DocumentSearch.BuildRerankWindows(hits);

        Assert.Single(windows);
        Assert.Equal("H\n\nshort body", windows[0]);
        Assert.Equal([0], owner);
    }

    [Fact]
    public void Long_chunks_split_into_overlapping_breadcrumbed_windows()
    {
        var body = string.Concat(Enumerable.Range(0, 180).Select(i => $"word{i:D4} ")); // ~1800 chars
        var hits = new List<SearchHit>
        {
            new(1, "p", "a", "Doc > Section", null, $"Doc > Section\n\n{body}", null),
        };

        var (windows, owner) = DocumentSearch.BuildRerankWindows(hits);

        Assert.True(windows.Count >= 2);
        Assert.All(windows, w => Assert.StartsWith("Doc > Section\n\n", w));
        Assert.All(owner, o => Assert.Equal(0, o));
        Assert.Contains("word0000", windows[0]);
        Assert.Contains(windows, w => w.Contains("word0179")); // tail is covered
        // Overlap: the second window starts before the first one's end.
        Assert.Contains(windows[0][^150..^100].Trim().Split(' ')[0], windows[1]);
    }

    [Fact]
    public void MaxPerHit_takes_the_best_window_for_each_hit()
    {
        // hit 0 has windows 0,1; hit 1 has window 2.
        var best = DocumentSearch.MaxPerHit([-5f, -1f, -3f], [0, 0, 1], hitCount: 2);

        Assert.Equal([-1f, -3f], best);
    }
}
