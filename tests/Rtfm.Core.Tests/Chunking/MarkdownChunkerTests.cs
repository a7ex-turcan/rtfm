using Rtfm.Core.Chunking;

namespace Rtfm.Core.Tests.Chunking;

public class MarkdownChunkerTests
{
    private static readonly ChunkMetadata Meta = new(
        SourcePath: "docs/sample.doc",
        DocumentTitle: "Doc Title",
        SourceModifiedAt: new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero));

    private static IReadOnlyList<Chunk> Chunk(string markdown, ChunkingOptions? options = null)
        => new MarkdownChunker(options).Chunk(markdown, Meta);

    [Fact]
    public void Splits_on_headings_and_builds_breadcrumbs()
    {
        const string md =
            "# Doc Title\n\nIntro paragraph.\n\n" +
            "## Section A\n\nContent A1.\n\n" +
            "### Sub A1\n\nDetail under sub.\n\n" +
            "## Section B\n\nContent B.\n";

        var chunks = Chunk(md);

        Assert.Equal(4, chunks.Count);
        Assert.Equal("Doc Title", chunks[0].HeadingPath);
        Assert.Contains("Intro paragraph.", chunks[0].Text);
        Assert.Equal("Doc Title > Section A", chunks[1].HeadingPath);
        Assert.Equal("Doc Title > Section A > Sub A1", chunks[2].HeadingPath);
        Assert.Equal("Doc Title > Section B", chunks[3].HeadingPath);
    }

    [Fact]
    public void Drops_pure_container_headings_but_keeps_ancestors_in_breadcrumb()
    {
        const string md = "# T\n\n## Parent\n\n### Child\n\nBody under child.\n";

        var chunks = Chunk(md);

        var only = Assert.Single(chunks);
        Assert.Equal("T > Parent > Child", only.HeadingPath);
        Assert.Contains("Body under child.", only.Text);
    }

    [Fact]
    public void Keeps_a_leaf_heading_that_has_no_body()
    {
        const string md = "# T\n\nintro\n\n## Empty Leaf\n";

        var chunks = Chunk(md);

        var leaf = Assert.Single(chunks, c => c.HeadingPath == "T > Empty Leaf");
        Assert.Equal(string.Empty, leaf.Text);
        Assert.Equal("T > Empty Leaf", leaf.ContentWithBreadcrumb); // breadcrumb-only content
    }

    [Fact]
    public void Large_section_splits_into_overlapping_windows()
    {
        var paragraphs = new[] { "Alpha alpha.", "Bravo bravo.", "Charlie charlie.", "Delta delta.", "Echo echo." };
        var md = "# Big\n\n" + string.Join("\n\n", paragraphs) + "\n";

        var chunks = Chunk(md, new ChunkingOptions(MaxChars: 30, OverlapChars: 15));

        Assert.True(chunks.Count > 1, "expected the oversized section to split");

        // Every paragraph survives somewhere.
        foreach (var p in paragraphs)
        {
            Assert.Contains(chunks, c => c.Text.Contains(p));
        }

        // Consecutive windows overlap by at least one shared paragraph.
        var overlapped = false;
        for (var i = 0; i + 1 < chunks.Count; i++)
        {
            if (paragraphs.Any(p => chunks[i].Text.Contains(p) && chunks[i + 1].Text.Contains(p)))
            {
                overlapped = true;
                break;
            }
        }

        Assert.True(overlapped, "expected overlap between consecutive windows");
    }

    [Fact]
    public void Oversized_table_splits_by_rows_and_repeats_the_header()
    {
        var rows = string.Join("\n", Enumerable.Range(1, 20).Select(i => $"| item{i} | value{i} |"));
        var md = "# T\n\n| Name | Value |\n| --- | --- |\n" + rows + "\n";

        var chunks = Chunk(md, new ChunkingOptions(MaxChars: 120, OverlapChars: 20));

        Assert.True(chunks.Count > 1, "expected the oversized table to split");
        Assert.All(chunks, c => Assert.Contains("| Name | Value |", c.Text)); // header repeated
        Assert.All(chunks, c => Assert.True(c.Text.Length <= 200));           // sane sizes
        for (var i = 1; i <= 20; i++)
        {
            Assert.Contains(chunks, c => c.Text.Contains($"| item{i} | value{i} |"));
        }
    }

    [Fact]
    public void Carries_document_metadata_onto_every_chunk()
    {
        var chunks = Chunk("# T\n\nbody\n\n## S\n\nmore\n");

        Assert.All(chunks, c =>
        {
            Assert.Equal(Meta.SourcePath, c.SourcePath);
            Assert.Equal(Meta.DocumentTitle, c.DocumentTitle);
            Assert.Equal(Meta.SourceModifiedAt, c.SourceModifiedAt);
        });
        Assert.Equal(new[] { 0, 1 }, chunks.Select(c => c.Ordinal).ToArray());
    }

    [Fact]
    public void Ignores_hash_lines_inside_code_fences()
    {
        const string md = "# T\n\nintro\n\n```\n# not a heading\n```\n\nafter\n";

        var chunks = Chunk(md);

        var only = Assert.Single(chunks);
        Assert.Equal("T", only.HeadingPath);
        Assert.Contains("# not a heading", only.Text);
        Assert.Contains("after", only.Text);
    }
}
