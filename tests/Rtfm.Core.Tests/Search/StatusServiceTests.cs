using System.Text.Json;
using Rtfm.Core.Search;

namespace Rtfm.Core.Tests.Search;

public class StatusServiceTests
{
    [Fact]
    public void Status_query_aggregates_per_project_with_coverage_and_recency()
    {
        using var doc = JsonDocument.Parse(StatusService.BuildStatusQuery(project: null));
        var projects = doc.RootElement.GetProperty("aggs").GetProperty("projects");

        Assert.Equal("project", projects.GetProperty("terms").GetProperty("field").GetString());

        var sub = projects.GetProperty("aggs");
        Assert.Equal("source_path", sub.GetProperty("docs").GetProperty("cardinality").GetProperty("field").GetString());
        Assert.Equal("source_modified_at", sub.GetProperty("oldest").GetProperty("min").GetProperty("field").GetString());
        Assert.Equal("source_modified_at", sub.GetProperty("newest").GetProperty("max").GetProperty("field").GetString());
        Assert.Equal("indexed_at", sub.GetProperty("last_indexed").GetProperty("max").GetProperty("field").GetString());
        Assert.Equal("content_vector", sub.GetProperty("embedded").GetProperty("filter").GetProperty("exists").GetProperty("field").GetString());
    }

    [Fact]
    public void Scoped_status_query_filters_by_project()
    {
        using var doc = JsonDocument.Parse(StatusService.BuildStatusQuery("pam"));
        Assert.Equal("pam", doc.RootElement.GetProperty("query").GetProperty("term").GetProperty("project").GetString());
    }

    [Fact]
    public void Parses_buckets_into_project_statuses()
    {
        const string response =
            """
            {
              "hits": { "total": { "value": 116 } },
              "aggregations": {
                "projects": {
                  "buckets": [
                    {
                      "key": "pam",
                      "doc_count": 111,
                      "docs": { "value": 5 },
                      "embedded": { "doc_count": 111 },
                      "oldest": { "value": 1781654400000, "value_as_string": "2026-06-17T00:00:00.000Z" },
                      "newest": { "value": 1781827200000, "value_as_string": "2026-06-19T00:00:00.000Z" },
                      "last_indexed": { "value": 1782988200000, "value_as_string": "2026-07-02T12:30:00.000Z" }
                    },
                    {
                      "key": "sparse",
                      "doc_count": 5,
                      "docs": { "value": 1 },
                      "embedded": { "doc_count": 0 },
                      "oldest": { "value": null },
                      "newest": { "value": null },
                      "last_indexed": { "value": null }
                    }
                  ]
                }
              }
            }
            """;

        var statuses = StatusService.ParseStatuses(response);

        Assert.Equal(2, statuses.Count);

        var pam = statuses[0];
        Assert.Equal("pam", pam.Project);
        Assert.Equal(5, pam.DocCount);
        Assert.Equal(111, pam.ChunkCount);
        Assert.Equal(1.0, pam.VectorCoverage, precision: 6);
        Assert.Equal(new DateTimeOffset(2026, 6, 17, 0, 0, 0, TimeSpan.Zero), pam.OldestSourceModified);
        Assert.Equal(new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero), pam.NewestSourceModified);
        Assert.Equal(new DateTimeOffset(2026, 7, 2, 12, 30, 0, TimeSpan.Zero), pam.LastIndexedAt);

        var sparse = statuses[1];
        Assert.Equal(0.0, sparse.VectorCoverage);
        Assert.Null(sparse.OldestSourceModified);
        Assert.Null(sparse.LastIndexedAt);
    }
}
