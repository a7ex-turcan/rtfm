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

        Assert.Equal("keyword", props.GetProperty("project").GetProperty("type").GetString());
        Assert.Equal("keyword", props.GetProperty("source_path").GetProperty("type").GetString());
        Assert.Equal("date", props.GetProperty("source_modified_at").GetProperty("type").GetString());
        Assert.Equal("date", props.GetProperty("indexed_at").GetProperty("type").GetString());
        Assert.Equal("knn_vector", props.GetProperty("content_vector").GetProperty("type").GetString());
        Assert.Equal("keyword", props.GetProperty("content_hash").GetProperty("type").GetString());
        Assert.Equal("keyword", props.GetProperty("line_hashes").GetProperty("type").GetString());

        // The additions snippet (PUT _mapping onto pre-Phase-22 indexes) must
        // agree with the create-time definition.
        using var additions = JsonDocument.Parse(RtfmIndex.MappingAdditionsJson);
        Assert.Equal("keyword", additions.RootElement.GetProperty("properties").GetProperty("content_hash").GetProperty("type").GetString());
        Assert.Equal("keyword", additions.RootElement.GetProperty("properties").GetProperty("line_hashes").GetProperty("type").GetString());

        var analyzers = doc.RootElement.GetProperty("settings").GetProperty("analysis").GetProperty("analyzer");
        Assert.True(analyzers.TryGetProperty("rtfm_technical", out _));
    }

    [Fact]
    public void Bulk_payload_pairs_action_and_document_lines_with_deterministic_ids()
    {
        var indexedAt = new DateTimeOffset(2026, 7, 1, 12, 0, 0, TimeSpan.Zero);
        var chunks = new List<Chunk>
        {
            new(0, "d:/docs/a.doc", "A > B", "body one", "A", new DateTimeOffset(2026, 6, 19, 0, 0, 0, TimeSpan.Zero), Project: "payments"),
            new(1, "d:/docs/a.doc", "A > C", "body two", DocumentTitle: null, SourceModifiedAt: null, Project: "payments"),
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
            Assert.Equal("payments", root.GetProperty("project").GetString());
            Assert.Equal("A", root.GetProperty("title").GetString());
            Assert.Equal("A > B\n\nbody one", root.GetProperty("content").GetString());
            Assert.True(root.TryGetProperty("source_modified_at", out _));
            Assert.True(root.TryGetProperty("indexed_at", out _));
            // Phase 22: fingerprints of the normalized body text, for template counting.
            Assert.Equal(Core.Contradictions.ContradictionDetector.ContentHash("body one"), root.GetProperty("content_hash").GetString());
            Assert.Equal(JsonValueKind.Array, root.GetProperty("line_hashes").ValueKind);
        }

        // Null title/date are omitted (WhenWritingNull), not written as null.
        using (var second = JsonDocument.Parse(lines[3]))
        {
            Assert.False(second.RootElement.TryGetProperty("title", out _));
            Assert.False(second.RootElement.TryGetProperty("source_modified_at", out _));
        }

        // No vectors supplied → no content_vector field (Tier 1 shape unchanged).
        using (var first = JsonDocument.Parse(lines[1]))
        {
            Assert.False(first.RootElement.TryGetProperty("content_vector", out _));
        }
    }

    [Fact]
    public void Bulk_payload_carries_vectors_when_supplied()
    {
        var chunks = new List<Chunk> { new(0, "d:/docs/a.doc", "A", "body", "A", null) };
        List<float[]> vectors = [[0.25f, -0.5f]];

        var payload = DocumentIndexer.BuildBulkPayload(chunks, DateTimeOffset.UnixEpoch, vectors);
        var lines = payload.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        using var doc = JsonDocument.Parse(lines[1]);
        var vector = doc.RootElement.GetProperty("content_vector");
        Assert.Equal(2, vector.GetArrayLength());
        Assert.Equal(0.25f, vector[0].GetSingle());
        Assert.Equal(-0.5f, vector[1].GetSingle());
    }

    [Fact]
    public void Bulk_payload_rejects_misaligned_vectors()
    {
        var chunks = new List<Chunk> { new(0, "d:/docs/a.doc", "A", "body", "A", null) };

        Assert.Throws<ArgumentException>(
            () => DocumentIndexer.BuildBulkPayload(chunks, DateTimeOffset.UnixEpoch, vectors: []));
    }
}
