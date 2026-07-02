using System.Text.Json;
using Rtfm.Core.Search;

namespace Rtfm.Core.Tests.Search;

public class DocumentSearchTests
{
    [Fact]
    public void Builds_a_valid_bm25_multi_match_query()
    {
        using var doc = JsonDocument.Parse(DocumentSearch.BuildQuery("GET /Bundle", topK: 7));
        var root = doc.RootElement;

        Assert.Equal(7, root.GetProperty("size").GetInt32());

        var multiMatch = root.GetProperty("query").GetProperty("multi_match");
        Assert.Equal("GET /Bundle", multiMatch.GetProperty("query").GetString());

        var fields = multiMatch.GetProperty("fields").EnumerateArray().Select(f => f.GetString()).ToArray();
        Assert.Contains("content", fields);
        Assert.Contains("heading_path^2", fields);
    }

    [Fact]
    public void No_project_means_no_filter_clause()
    {
        using var doc = JsonDocument.Parse(DocumentSearch.BuildQuery("anything", topK: 5, project: null));
        // Bare multi_match, no bool/filter.
        Assert.True(doc.RootElement.GetProperty("query").TryGetProperty("multi_match", out _));
    }

    [Fact]
    public void A_project_adds_a_term_filter()
    {
        using var doc = JsonDocument.Parse(DocumentSearch.BuildQuery("anything", topK: 5, project: "payments"));
        var boolQuery = doc.RootElement.GetProperty("query").GetProperty("bool");

        Assert.True(boolQuery.GetProperty("must")[0].TryGetProperty("multi_match", out _));
        var term = boolQuery.GetProperty("filter")[0].GetProperty("term");
        Assert.Equal("payments", term.GetProperty("project").GetString());
    }
}
