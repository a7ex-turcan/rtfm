namespace Rtfm.Core.Notes;

/// <summary>
/// The overrides/annotations index (§2.13 C — option 2, resolved in Phase 13).
/// User-confirmed corrections live here, *not* in <c>rtfm-docs</c>, so the main
/// index stays purely derived and §2.9's delete-and-reindex can never wipe a
/// correction. Notes are searched at query time and merged into results,
/// always attributed as overrides. The analyzer + vector field mirror the main
/// index (same model embeds both) so lexical and semantic matching behave
/// identically for note text.
/// </summary>
public static class NotesIndex
{
    public const string Name = "rtfm-notes";

    public static string DefinitionJson =>
        $$"""
        {
          "settings": {
            "index": { "knn": true },
            "analysis": {
              "analyzer": {
                "rtfm_technical": {
                  "type": "custom",
                  "tokenizer": "whitespace",
                  "filter": ["rtfm_word_delimiter", "lowercase", "flatten_graph"]
                }
              },
              "filter": {
                "rtfm_word_delimiter": {
                  "type": "word_delimiter_graph",
                  "generate_word_parts": true,
                  "generate_number_parts": true,
                  "catenate_words": false,
                  "catenate_numbers": false,
                  "split_on_case_change": true,
                  "split_on_numerics": true,
                  "preserve_original": true,
                  "stem_english_possessive": false
                }
              }
            }
          },
          "mappings": {
            "properties": {
              "note_id":        { "type": "keyword" },
              "project":        { "type": "keyword" },
              "text":           { "type": "text", "analyzer": "rtfm_technical" },
              "target_path":    { "type": "keyword" },
              "author":         { "type": "keyword" },
              "created_at":     { "type": "date" },
              "content_vector": { "type": "knn_vector", "dimension": {{Indexing.RtfmIndex.VectorDimension}},
                                  "method": { "name": "hnsw", "space_type": "l2", "engine": "lucene" } }
            }
          }
        }
        """;
}
