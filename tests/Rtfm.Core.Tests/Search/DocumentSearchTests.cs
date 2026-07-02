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
}
