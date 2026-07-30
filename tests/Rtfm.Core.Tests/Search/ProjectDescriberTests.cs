using System.Text.Json;
using Rtfm.Core.Contradictions;
using Rtfm.Core.Search;

namespace Rtfm.Core.Tests.Search;

public class ProjectDescriberTests
{
    [Theory]
    [InlineData("d:/docs/pam/rbac.doc", "doc")]
    [InlineData("d:/docs/pam/schema.SQL", "sql")]
    [InlineData("d:/docs/pam/diagram.drawio.png", "png")]
    [InlineData("d:/docs/pam/readme", "(no extension)")]
    public void Kind_comes_from_the_file_extension(string path, string expected)
        => Assert.Equal(expected, ProjectDescriber.ClassifyKind(path));

    [Theory]
    [InlineData("jira://AEXP-221", "jira")]
    [InlineData("confluence://123456", "confluence")]
    public void Synthetic_source_keys_are_named_by_their_product(string path, string expected)
        => Assert.Equal(expected, ProjectDescriber.ClassifyKind(path));

    [Fact]
    public void Kinds_group_documents_and_chunks_biggest_first()
    {
        var documents = new[]
        {
            new SourceInfo("d:/docs/a.doc", "A", "pam", null, 25),
            new SourceInfo("d:/docs/b.doc", "B", "pam", null, 10),
            new SourceInfo("d:/docs/c.pdf", "C", "pam", null, 40),
            new SourceInfo("jira://AEXP-1", "Ticket", "pam", null, 3),
        };

        var kinds = ProjectDescriber.SummarizeKinds(documents);

        Assert.Collection(
            kinds,
            doc =>
            {
                Assert.Equal("doc", doc.Kind);
                Assert.Equal(2, doc.Documents);
                Assert.Equal(35, doc.Chunks);
            },
            // Ties break alphabetically, so jira precedes pdf at one document each.
            jira => Assert.Equal("jira", jira.Kind),
            pdf =>
            {
                Assert.Equal("pdf", pdf.Kind);
                Assert.Equal(40, pdf.Chunks);
            });
    }

    [Fact]
    public void Contradiction_summary_query_defaults_missing_lifecycle_fields()
    {
        using var doc = JsonDocument.Parse(ProjectDescriber.BuildContradictionSummaryQuery("pam"));
        var root = doc.RootElement;

        Assert.Equal("pam", root.GetProperty("query").GetProperty("term").GetProperty("project").GetString());

        // Pre-Phase-22 pairs carry neither field; absence must read as open/contradiction.
        var statuses = root.GetProperty("aggs").GetProperty("statuses").GetProperty("terms");
        Assert.Equal("status", statuses.GetProperty("field").GetString());
        Assert.Equal(ContradictionPair.StatusOpen, statuses.GetProperty("missing").GetString());

        var openKinds = root.GetProperty("aggs").GetProperty("open_kinds");
        var excluded = openKinds.GetProperty("filter").GetProperty("bool").GetProperty("must_not")[0]
            .GetProperty("terms").GetProperty("status")
            .EnumerateArray().Select(e => e.GetString() ?? string.Empty).ToArray();
        Assert.Equal([ContradictionPair.StatusDismissed, ContradictionPair.StatusResolved], excluded);
        Assert.Equal(
            ContradictionPair.KindContradiction,
            openKinds.GetProperty("aggs").GetProperty("kinds").GetProperty("terms").GetProperty("missing").GetString());
    }

    [Fact]
    public void Contradiction_summary_splits_open_supersessions_from_closed()
    {
        const string response =
            """
            {
              "aggregations": {
                "statuses": {
                  "buckets": [
                    { "key": "open",      "doc_count": 7 },
                    { "key": "dismissed", "doc_count": 3 },
                    { "key": "resolved",  "doc_count": 1 }
                  ]
                },
                "open_kinds": {
                  "doc_count": 7,
                  "kinds": {
                    "buckets": [
                      { "key": "contradiction",       "doc_count": 5 },
                      { "key": "likely-supersession", "doc_count": 2 }
                    ]
                  }
                }
              }
            }
            """;

        var summary = ProjectDescriber.ParseContradictionSummary(response);

        Assert.Equal(7, summary.Open);
        Assert.Equal(2, summary.LikelySupersession);
        Assert.Equal(3, summary.Dismissed);
        Assert.Equal(1, summary.Resolved);
        Assert.Equal(11, summary.Total);
    }

    [Fact]
    public void Contradiction_summary_is_empty_when_the_side_index_has_no_aggregations()
        => Assert.Equal(ContradictionSummary.Empty, ProjectDescriber.ParseContradictionSummary("""{"hits":{"total":{"value":0}}}"""));
}
