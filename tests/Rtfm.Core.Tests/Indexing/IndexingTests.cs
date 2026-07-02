using System.Text.Json;
using Rtfm.Core.Chunking;
using Rtfm.Core.Indexing;

namespace Rtfm.Core.Tests.Indexing;

public class IndexingTests
{
    [Fact]
    public void Index_definition_is_valid_json_with_expected_fields()
    {
        using var doc = JsonDocument.Parse(RtfmIndex.DefinitionJson);
        var props = doc.RootElement.GetProperty("mappings").GetProperty("properties");

        Assert.Equal("keyword", props.GetProperty("source_path").GetProperty("type").GetString());
        Assert.Equal("date", props.GetProperty("source_modified_at").GetProperty("type").GetString());
        Assert.Equal("date", props.GetProperty("indexed_at").GetProperty("type").GetString());
        Assert.Equal("knn_vector", props.GetProperty("content_vector").GetProperty("type").GetString());

        var analyzers = doc.RootElement.GetProperty("settings").GetProperty("analysis").GetProperty("analyzer");
        Assert.True(analyzers.TryGetProperty("rtfm_technical", out _));
    }

    [Fact]
    public void Bulk_payload_pairs_action_and_document_lines_with_deterministic_ids()
    {
        var indexedAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var chunks = new List<Chunk>
        {
            new(0, "d:/docs/a.doc", "A > B", "body one", "A", new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero)),
            new(1, "d:/docs/a.doc", "A > C", "body two", DocumentTitle: null, SourceModifiedAt: null),
        };

        var payload = DocumentIndexer.BuildBulkPayload(chunks, indexedAt);
        var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal(4, lines.Length); // action + doc per chunk

        Assert.Contains("\"_id\":\"d:/docs/a.doc#0\"", lines[0]);
        Assert.Contains("\"_id\":\"d:/docs/a.doc#1\"", lines[2]);

        // First doc carries title + source_modified_at + indexed_at.
        using (var first = JsonDocument.Parse(lines[1]))
        {
            var root = first.RootElement;
            Assert.Equal("A", root.GetProperty("title").GetString());
            Assert.Equal("A > B\n\nbody one", root.GetProperty("content").GetString());
            Assert.True(root.TryGetProperty("source_modified_at", out _));
            Assert.True(root.TryGetProperty("indexed_at", out _));
        }

        // Null title/date are omitted (WhenWritingNull), not written as null.
        using (var second = JsonDocument.Parse(lines[3]))
        {
            Assert.False(second.RootElement.TryGetProperty("title", out _));
            Assert.False(second.RootElement.TryGetProperty("source_modified_at", out _));
        }
    }
}
