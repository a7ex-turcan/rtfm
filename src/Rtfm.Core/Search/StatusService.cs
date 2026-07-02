using System.Text.Json;
using Rtfm.Core.Indexing;
using Rtfm.Core.OpenSearch;

namespace Rtfm.Core.Search;

/// <summary>One project's index health (Phase 10): counts, recency spread, vector coverage.</summary>
public sealed record ProjectStatus(
    string Project,
    long DocCount,
    long ChunkCount,
    long EmbeddedChunkCount,
    DateTimeOffset? OldestSourceModified,
    DateTimeOffset? NewestSourceModified,
    DateTimeOffset? LastIndexedAt)
{
    /// <summary>Fraction of chunks carrying a vector (1.0 = fully semantic-searchable).</summary>
    public double VectorCoverage => ChunkCount == 0 ? 0 : (double)EmbeddedChunkCount / ChunkCount;
}

/// <summary>
/// Read-only observability over the index (Phase 10) — one aggregation query
/// answering "what's in there, how fresh, and is the semantic tier populated".
/// Everything is derived from fields every chunk already carries; staleness
/// listing reuses <see cref="DocumentCatalog.ListSourcesAsync"/> rather than a
/// new query.
/// </summary>
public sealed class StatusService(OpenSearchGateway gateway)
{
    /// <summary>Per-project rollups, optionally scoped to one project. Empty when the index doesn't exist yet.</summary>
    public async Task<IReadOnlyList<ProjectStatus>> GetProjectStatusesAsync(string? project = null, CancellationToken cancellationToken = default)
    {
        if (!await gateway.IndexExistsAsync(RtfmIndex.Name, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var json = await gateway.SearchAsync(RtfmIndex.Name, BuildStatusQuery(project), cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseStatuses(json);
    }

    internal static string BuildStatusQuery(string? project)
    {
        object query = string.IsNullOrWhiteSpace(project)
            ? new { match_all = new { } }
            : new { term = new Dictionary<string, string> { ["project"] = project } };

        var request = new
        {
            size = 0,
            query,
            aggs = new
            {
                projects = new
                {
                    terms = new { field = "project", size = 100, order = new { _key = "asc" } },
                    aggs = new
                    {
                        docs = new { cardinality = new { field = "source_path" } },
                        oldest = new { min = new { field = "source_modified_at" } },
                        newest = new { max = new { field = "source_modified_at" } },
                        last_indexed = new { max = new { field = "indexed_at" } },
                        embedded = new { filter = new { exists = new { field = "content_vector" } } },
                    },
                },
            },
        };

        return JsonSerializer.Serialize(request);
    }

    internal static IReadOnlyList<ProjectStatus> ParseStatuses(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var statuses = new List<ProjectStatus>();

        if (!doc.RootElement.TryGetProperty("aggregations", out var aggs))
        {
            return statuses;
        }

        foreach (var bucket in aggs.GetProperty("projects").GetProperty("buckets").EnumerateArray())
        {
            statuses.Add(new ProjectStatus(
                Project: bucket.GetProperty("key").GetString() ?? string.Empty,
                DocCount: bucket.GetProperty("docs").GetProperty("value").GetInt64(),
                ChunkCount: bucket.GetProperty("doc_count").GetInt64(),
                EmbeddedChunkCount: bucket.GetProperty("embedded").GetProperty("doc_count").GetInt64(),
                OldestSourceModified: AggDate(bucket, "oldest"),
                NewestSourceModified: AggDate(bucket, "newest"),
                LastIndexedAt: AggDate(bucket, "last_indexed")));
        }

        return statuses;
    }

    /// <summary>min/max aggs return epoch millis + value_as_string; null when no docs carried the field.</summary>
    private static DateTimeOffset? AggDate(JsonElement bucket, string name)
    {
        var agg = bucket.GetProperty(name);
        if (agg.TryGetProperty("value_as_string", out var text)
            && text.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(text.GetString(), out var parsed))
        {
            return parsed;
        }

        return agg.TryGetProperty("value", out var millis) && millis.ValueKind == JsonValueKind.Number
            ? DateTimeOffset.FromUnixTimeMilliseconds((long)millis.GetDouble())
            : null;
    }
}
