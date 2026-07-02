namespace Rtfm.Core.Indexing;

/// <summary>
/// The OpenSearch index name and its create-time settings + mapping (§2.10).
/// Written as raw JSON on purpose — the analyzer/knn config is clearer this way
/// than fighting the strongly-typed client (§2.10 note).
///
/// Tier 1 today: a <c>rtfm_technical</c> analyzer that keeps technical tokens
/// searchable — a <c>whitespace</c> tokenizer holds `getUserAccessKeys` and
/// `/Bundle` together, then <c>word_delimiter_graph</c> also emits the parts
/// (<c>get</c>, <c>user</c>, …, <c>Bundle</c>) while <c>preserve_original</c>
/// keeps the whole token. The <c>content_vector</c> knn field is defined but
/// left unpopulated so Tier 2 is a backfill, not a reindex.
/// </summary>
public static class RtfmIndex
{
    public const string Name = "rtfm-docs";

    /// <summary>Embedding dimension reserved for Tier 2 (e.g. bge-small / MiniLM = 384).</summary>
    public const int VectorDimension = 384;

    /// <summary>Search pipeline that fuses the hybrid query's BM25 + kNN scores (§2.10 Tier 2).</summary>
    public const string HybridPipelineName = "rtfm-hybrid";

    /// <summary>
    /// Score fusion for hybrid queries. OpenSearch 2.17 has no RRF processor
    /// (that shipped in 2.19), so per §2.10 this is the score-normalization
    /// route: min–max normalize each sub-query's scores to [0,1], then combine
    /// with an equal-weight arithmetic mean. Weight order matches the hybrid
    /// query's <c>queries</c> array: [lexical, semantic].
    /// </summary>
    public const string HybridPipelineJson =
        """
        {
          "description": "RTFM hybrid score fusion: min_max normalization, equal-weight arithmetic mean (BM25 + kNN)",
          "phase_results_processors": [
            {
              "normalization-processor": {
                "normalization": { "technique": "min_max" },
                "combination": {
                  "technique": "arithmetic_mean",
                  "parameters": { "weights": [0.5, 0.5] }
                }
              }
            }
          ]
        }
        """;

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
              "project":            { "type": "keyword" },
              "source_path":        { "type": "keyword" },
              "ordinal":            { "type": "integer" },
              "title":              { "type": "text", "analyzer": "rtfm_technical",
                                      "fields": { "keyword": { "type": "keyword", "ignore_above": 512 } } },
              "heading_path":       { "type": "text", "analyzer": "rtfm_technical",
                                      "fields": { "keyword": { "type": "keyword", "ignore_above": 1024 } } },
              "content":            { "type": "text", "analyzer": "rtfm_technical" },
              "source_modified_at": { "type": "date" },
              "indexed_at":         { "type": "date" },
              "content_vector":     { "type": "knn_vector", "dimension": {{VectorDimension}},
                                      "method": { "name": "hnsw", "space_type": "l2", "engine": "lucene" } }
            }
          }
        }
        """;
}
