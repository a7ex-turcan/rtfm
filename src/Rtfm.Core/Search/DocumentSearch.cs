using System.Text.Json;
using Rtfm.Core.Indexing;
using Rtfm.Core.OpenSearch;

namespace Rtfm.Core.Search;

/// <summary>
/// Tier 1 retrieval (§2.10): a BM25 <c>multi_match</c> over the analyzed
/// <c>content</c>, with the heading breadcrumb and title boosted. Hybrid kNN is
/// Tier 2 and slots in here later without changing callers.
/// </summary>
public sealed class DocumentSearch(OpenSearchGateway gateway)
{
    public async Task<IReadOnlyList<SearchHit>> SearchAsync(string query, int topK = 5, CancellationToken cancellationToken = default)
    {
        var body = BuildQuery(query, topK);
        var json = await gateway.SearchAsync(RtfmIndex.Name, body, cancellationToken).ConfigureAwait(false);
        return ParseHits(json);
    }

    internal static string BuildQuery(string query, int topK)
    {
        var request = new
        {
            size = topK,
            query = new
            {
                multi_match = new
                {
                    query,
                    fields = new[] { "content", "heading_path^2", "title^2" },
                    type = "best_fields",
                },
            },
            _source = new[] { "source_path", "heading_path", "title", "content", "source_modified_at" },
        };

        return JsonSerializer.Serialize(request);
    }

    private static List<SearchHit> ParseHits(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var hits = new List<SearchHit>();

        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var source = hit.GetProperty("_source");
            hits.Add(new SearchHit(
                Score: hit.TryGetProperty("_score", out var score) && score.ValueKind == JsonValueKind.Number ? score.GetDouble() : 0,
                SourcePath: GetString(source, "source_path") ?? string.Empty,
                HeadingPath: GetString(source, "heading_path") ?? string.Empty,
                Title: GetString(source, "title"),
                Content: GetString(source, "content") ?? string.Empty,
                SourceModifiedAt: TryGetDate(source, "source_modified_at")));
        }

        return hits;
    }

    private static string? GetString(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? TryGetDate(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
}
