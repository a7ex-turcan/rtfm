namespace Rtfm.Core.Contradictions;

/// <summary>
/// The side index holding nominated contradiction pairs (§2.13, Phase 12). A
/// *separate* index because pairs must survive re-indexing of either document
/// (the main index is wiped per-doc on every re-ingest, §2.9); pairs are
/// instead deleted-and-re-evaluated when a doc they reference is re-ingested
/// or removed. Excerpts are stored for display, never searched.
/// </summary>
public static class ContradictionIndex
{
    public const string Name = "rtfm-contradictions";

    public const string DefinitionJson =
        """
        {
          "mappings": {
            "properties": {
              "project":       { "type": "keyword" },
              "similarity":    { "type": "float" },
              "detected_at":   { "type": "date" },
              "a_path":        { "type": "keyword" },
              "a_ordinal":     { "type": "integer" },
              "a_heading":     { "type": "keyword", "ignore_above": 1024 },
              "a_modified_at": { "type": "date" },
              "a_excerpt":     { "type": "text", "index": false },
              "b_path":        { "type": "keyword" },
              "b_ordinal":     { "type": "integer" },
              "b_heading":     { "type": "keyword", "ignore_above": 1024 },
              "b_modified_at": { "type": "date" },
              "b_excerpt":     { "type": "text", "index": false }
            }
          }
        }
        """;
}
