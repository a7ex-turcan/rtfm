using System.Text.Json;
using Rtfm.Core.Search;

namespace Rtfm.Core.Tests.Search;

public class DocumentCatalogTests
{
    [Fact]
    public void ReassembleMarkdown_dedupes_headings_and_strips_breadcrumbs()
    {
        var chunks = new List<(string HeadingPath, string Content)>
        {
            ("Doc", "Doc\n\nIntro paragraph."),                        // heading == title → no ## line
            ("Doc > Setup", "Doc > Setup\n\nStep one."),
            ("Doc > Setup", "Doc > Setup\n\nStep two (split chunk)."), // same heading → emitted once
            ("Doc > Usage", "Doc > Usage"),                            // container chunk: breadcrumb only, no body
        };

        var markdown = DocumentCatalog.ReassembleMarkdown("Doc", chunks);

        Assert.Equal(
            """
            # Doc

            Intro paragraph.

            ## Doc > Setup

            Step one.

            Step two (split chunk).

            ## Doc > Usage

            """.ReplaceLineEndings("\n").TrimEnd() + "\n",
            markdown);
    }

    [Fact]
    public void StripBreadcrumb_handles_all_three_content_shapes()
    {
        Assert.Equal("body", DocumentCatalog.StripBreadcrumb("A > B\n\nbody", "A > B"));
        Assert.Equal(string.Empty, DocumentCatalog.StripBreadcrumb("A > B", "A > B"));
        // Unexpected shape → returned untouched rather than mangled.
        Assert.Equal("odd content", DocumentCatalog.StripBreadcrumb("odd content", "A > B"));
    }

    [Fact]
    public void MeanVector_averages_then_normalizes()
    {
        var mean = DocumentCatalog.MeanVector([[1f, 0f], [0f, 1f]]);

        Assert.Equal(Math.Sqrt(2) / 2, mean[0], precision: 6);
        Assert.Equal(Math.Sqrt(2) / 2, mean[1], precision: 6);
    }

    [Fact]
    public void NormalizeLookupPath_fixes_separators_and_case_without_touching_the_filesystem()
    {
        Assert.Equal("d:/docs/rbac.doc", DocumentCatalog.NormalizeLookupPath(@" D:\Docs\RBAC.doc "));
        Assert.Equal("rbac.doc", DocumentCatalog.LastSegment("d:/docs/rbac.doc"));
        Assert.Equal("rbac.doc", DocumentCatalog.LastSegment("rbac.doc")); // bare filename
    }

    [Fact]
    public void Similar_query_excludes_self_and_scopes_project()
    {
        var body = DocumentCatalog.BuildSimilarQuery([0.1f, 0.2f], "d:/docs/self.doc", "pam");

        using var doc = JsonDocument.Parse(body);
        var knn = doc.RootElement.GetProperty("query").GetProperty("knn").GetProperty("content_vector");
        var filter = knn.GetProperty("filter").GetProperty("bool");

        Assert.Equal("d:/docs/self.doc", filter.GetProperty("must_not")[0].GetProperty("term").GetProperty("source_path").GetString());
        Assert.Equal("pam", filter.GetProperty("filter")[0].GetProperty("term").GetProperty("project").GetString());
        Assert.Equal(2, knn.GetProperty("vector").GetArrayLength());
    }

    [Fact]
    public void Chunks_query_sorts_by_ordinal_and_falls_back_to_wildcard()
    {
        using (var exact = JsonDocument.Parse(DocumentCatalog.BuildChunksQuery("d:/docs/a.doc", exact: true, project: null, withVectors: false)))
        {
            Assert.Equal("asc", exact.RootElement.GetProperty("sort")[0].GetProperty("ordinal").GetString());
            Assert.Equal("d:/docs/a.doc",
                exact.RootElement.GetProperty("query").GetProperty("bool").GetProperty("filter")[0].GetProperty("term").GetProperty("source_path").GetString());
        }

        using (var byName = JsonDocument.Parse(DocumentCatalog.BuildChunksQuery("a.doc", exact: false, project: "pam", withVectors: true)))
        {
            var filters = byName.RootElement.GetProperty("query").GetProperty("bool").GetProperty("filter");
            Assert.Equal("*a.doc", filters[0].GetProperty("wildcard").GetProperty("source_path").GetProperty("value").GetString());
            Assert.Equal("pam", filters[1].GetProperty("term").GetProperty("project").GetString());
            Assert.Contains("content_vector", byName.RootElement.GetProperty("_source").EnumerateArray().Select(e => e.GetString()));
        }
    }

    [Fact]
    public void Chunks_query_windows_on_ordinal_when_a_range_is_given()
    {
        using var doc = JsonDocument.Parse(
            DocumentCatalog.BuildChunksQuery("d:/docs/a.doc", exact: true, project: null, withVectors: false, ordinalRange: (3, 7)));

        var filters = doc.RootElement.GetProperty("query").GetProperty("bool").GetProperty("filter");
        var range = filters[1].GetProperty("range").GetProperty("ordinal");

        Assert.Equal(3, range.GetProperty("gte").GetInt32());
        Assert.Equal(7, range.GetProperty("lte").GetInt32());
        // Without a range, no range clause sneaks in.
        using var whole = JsonDocument.Parse(DocumentCatalog.BuildChunksQuery("d:/docs/a.doc", exact: true, project: null, withVectors: false));
        Assert.Equal(1, whole.RootElement.GetProperty("query").GetProperty("bool").GetProperty("filter").GetArrayLength());
    }

    [Fact]
    public void List_sources_query_aggregates_by_path_with_first_chunk_metadata()
    {
        using var doc = JsonDocument.Parse(DocumentCatalog.BuildListSourcesQuery("pam"));
        var docs = doc.RootElement.GetProperty("aggs").GetProperty("docs");

        Assert.Equal("source_path", docs.GetProperty("terms").GetProperty("field").GetString());
        Assert.Equal(1, docs.GetProperty("aggs").GetProperty("meta").GetProperty("top_hits").GetProperty("size").GetInt32());
        Assert.Equal("pam", doc.RootElement.GetProperty("query").GetProperty("term").GetProperty("project").GetString());
    }
}
