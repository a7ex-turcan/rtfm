using System.Text.Json;
using Rtfm.Core.Indexing;
using Rtfm.Core.Search;

namespace Rtfm.Core.Tests.Search;

public class HybridQueryTests
{
    [Fact]
    public void Hybrid_query_pairs_lexical_and_knn_clauses_in_pipeline_weight_order()
    {
        var body = DocumentSearch.BuildHybridQuery("what is a bundle", [0.1f, 0.2f], topK: 5);

        using var doc = JsonDocument.Parse(body);
        var root = doc.RootElement;

        Assert.Equal(5, root.GetProperty("size").GetInt32());

        var queries = root.GetProperty("query").GetProperty("hybrid").GetProperty("queries");
        Assert.Equal(2, queries.GetArrayLength());

        // Order must match the pipeline's weights: [lexical, semantic].
        Assert.True(queries[0].TryGetProperty("multi_match", out _));
        var knn = queries[1].GetProperty("knn").GetProperty("content_vector");
        Assert.Equal(2, knn.GetProperty("vector").GetArrayLength());
        Assert.Equal(25, knn.GetProperty("k").GetInt32()); // max(5*5, 25)
        Assert.False(knn.TryGetProperty("filter", out _)); // no project → no filter
    }

    [Fact]
    public void Hybrid_query_carries_the_project_filter_on_both_clauses()
    {
        var body = DocumentSearch.BuildHybridQuery("query", [0.1f], topK: 5, project: "pam");

        using var doc = JsonDocument.Parse(body);
        var queries = doc.RootElement.GetProperty("query").GetProperty("hybrid").GetProperty("queries");

        // Lexical clause: wrapped in bool + term filter.
        var lexicalFilter = queries[0].GetProperty("bool").GetProperty("filter")[0].GetProperty("term").GetProperty("project");
        Assert.Equal("pam", lexicalFilter.GetString());

        // knn clause: its own filter parameter.
        var knnFilter = queries[1].GetProperty("knn").GetProperty("content_vector").GetProperty("filter").GetProperty("term").GetProperty("project");
        Assert.Equal("pam", knnFilter.GetString());
    }

    [Fact]
    public void Knn_candidate_count_scales_with_topK_within_bounds()
    {
        static int K(int topK)
        {
            var body = DocumentSearch.BuildHybridQuery("q", [0f], topK);
            using var doc = JsonDocument.Parse(body);
            return doc.RootElement.GetProperty("query").GetProperty("hybrid").GetProperty("queries")[1]
                .GetProperty("knn").GetProperty("content_vector").GetProperty("k").GetInt32();
        }

        Assert.Equal(25, K(1));    // floor
        Assert.Equal(50, K(10));   // 10*5
        Assert.Equal(100, K(40));  // cap
    }

    [Fact]
    public void Hybrid_pipeline_definition_is_valid_min_max_normalization()
    {
        using var doc = JsonDocument.Parse(RtfmIndex.HybridPipelineJson);
        var processor = doc.RootElement.GetProperty("phase_results_processors")[0].GetProperty("normalization-processor");

        Assert.Equal("min_max", processor.GetProperty("normalization").GetProperty("technique").GetString());
        Assert.Equal("arithmetic_mean", processor.GetProperty("combination").GetProperty("technique").GetString());

        var weights = processor.GetProperty("combination").GetProperty("parameters").GetProperty("weights");
        Assert.Equal(2, weights.GetArrayLength()); // one per hybrid sub-query
    }
}
