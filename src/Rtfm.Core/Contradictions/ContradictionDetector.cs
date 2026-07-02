using System.Text;
using System.Text.Json;
using Rtfm.Core.Chunking;
using Rtfm.Core.Indexing;
using Rtfm.Core.OpenSearch;

namespace Rtfm.Core.Contradictions;

/// <summary>
/// Proactive contradiction detection (§2.13, Phase 12). At ingest, each new
/// chunk's vector is compared against similar chunks from <b>other documents
/// in the same project</b> (cross-project differences are expected, §2.14 —
/// never nominated). The judgment is deliberately dumb (§2.13: "start dumb"):
/// high vector similarity + different source dates + not near-identical text ⇒
/// nominate the best candidate per chunk into the side index. The LLM reasons
/// about whether it is a *real* contradiction at read time (§2.13 B); nothing
/// here resolves anything.
///
/// Lifecycle: pairs referencing a document are deleted whenever that document
/// is re-ingested (then re-evaluated from its fresh chunks) or removed — so
/// the side index survives §2.9's delete-and-reindex without going stale.
/// </summary>
public sealed class ContradictionDetector(OpenSearchGateway gateway)
{
    /// <summary>
    /// Minimum kNN score to consider (lucene l2 on unit vectors: score = 1/(1+d²)
    /// = 1/(3−2·cos). 0.75 ≈ cosine 0.83 — close paraphrases, not just same topic.
    /// </summary>
    internal const double MinScore = 0.75;

    private const int CandidatesPerChunk = 3;
    private const int ExcerptChars = 240;

    /// <summary>Creates the side index if missing. Returns true if created.</summary>
    public Task<bool> EnsureIndexAsync(CancellationToken cancellationToken = default)
        => gateway.EnsureIndexAsync(ContradictionIndex.Name, ContradictionIndex.DefinitionJson, cancellationToken);

    /// <summary>Drops every pair referencing <paramref name="normalizedPath"/> (either side).</summary>
    public Task<long> RemoveForPathAsync(string normalizedPath, CancellationToken cancellationToken = default)
    {
        var query = new
        {
            query = new
            {
                @bool = new
                {
                    should = new object[]
                    {
                        new { term = new Dictionary<string, string> { ["a_path"] = normalizedPath } },
                        new { term = new Dictionary<string, string> { ["b_path"] = normalizedPath } },
                    },
                    minimum_should_match = 1,
                },
            },
        };

        return gateway.DeleteByQueryAsync(ContradictionIndex.Name, JsonSerializer.Serialize(query), cancellationToken);
    }

    /// <summary>Drops every pair in a project (purge, §2.14). Safe when the side index doesn't exist yet.</summary>
    public async Task<long> PurgeProjectAsync(string project, CancellationToken cancellationToken = default)
    {
        if (!await gateway.IndexExistsAsync(ContradictionIndex.Name, cancellationToken).ConfigureAwait(false))
        {
            return 0;
        }

        return await gateway.DeleteByTermAsync(ContradictionIndex.Name, "project", project, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Evaluates a freshly-ingested document's chunks against the rest of its
    /// project and upserts nominated pairs. Call *after* the chunks are indexed
    /// and *after* <see cref="RemoveForPathAsync"/>. No-op without vectors
    /// (lexical-only run). Returns the number of pairs nominated.
    /// </summary>
    public async Task<int> DetectForDocumentAsync(
        IReadOnlyList<Chunk> chunks,
        IReadOnlyList<float[]>? vectors,
        DateTimeOffset detectedAt,
        CancellationToken cancellationToken = default)
    {
        if (vectors is null || chunks.Count == 0)
        {
            return 0;
        }

        // Docs indexed moments ago in the same batch must be visible to the
        // kNN candidate search — don't depend on the 1s auto-refresh.
        await gateway.RefreshAsync(RtfmIndex.Name, cancellationToken).ConfigureAwait(false);

        var pairs = new List<ContradictionPair>();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var body = BuildCandidateQuery(vectors[i], chunk.SourcePath, chunk.Project);
            var json = await gateway.SearchAsync(RtfmIndex.Name, body, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (EvaluateCandidates(chunk, json) is { } pair)
            {
                pairs.Add(pair);
            }
        }

        if (pairs.Count == 0)
        {
            return 0;
        }

        // Deterministic ids make this an upsert; two chunks of this doc can
        // nominate the same counterpart pair — last write wins, same content.
        await gateway.BulkAsync(BuildBulkPayload(pairs, detectedAt), cancellationToken).ConfigureAwait(false);
        await gateway.RefreshAsync(ContradictionIndex.Name, cancellationToken).ConfigureAwait(false);
        return pairs.Count;
    }

    /// <summary>Nominated pairs, newest detection first, optionally scoped to one project.</summary>
    public async Task<IReadOnlyList<ContradictionPair>> ListAsync(string? project = null, int topK = 50, CancellationToken cancellationToken = default)
    {
        if (!await gateway.IndexExistsAsync(ContradictionIndex.Name, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        object query = string.IsNullOrWhiteSpace(project)
            ? new { match_all = new { } }
            : new { term = new Dictionary<string, string> { ["project"] = project } };

        var body = JsonSerializer.Serialize(new
        {
            size = topK,
            query,
            sort = new object[] { new { detected_at = "desc" }, new { similarity = "desc" } },
        });

        var json = await gateway.SearchAsync(ContradictionIndex.Name, body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParsePairs(json);
    }

    // ---- pure logic (internal for tests) ----

    /// <summary>Picks the best qualifying candidate for one chunk, or null.</summary>
    internal static ContradictionPair? EvaluateCandidates(Chunk chunk, string searchJson)
    {
        using var doc = JsonDocument.Parse(searchJson);

        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var score = hit.TryGetProperty("_score", out var s) && s.ValueKind == JsonValueKind.Number ? s.GetDouble() : 0;
            if (score < MinScore)
            {
                break; // hits are score-ordered; nothing further qualifies
            }

            var source = hit.GetProperty("_source");
            var otherHeading = GetString(source, "heading_path") ?? string.Empty;
            var otherContent = GetString(source, "content") ?? string.Empty;
            var otherText = StripBreadcrumb(otherContent, otherHeading);
            var otherModified = TryGetDate(source, "source_modified_at");

            if (!ShouldNominate(chunk.Text, chunk.SourceModifiedAt, otherText, otherModified))
            {
                continue;
            }

            var mine = new ContradictionSide(chunk.SourcePath, chunk.Ordinal, chunk.HeadingPath, chunk.SourceModifiedAt, Excerpt(chunk.Text));
            var theirs = new ContradictionSide(
                GetString(source, "source_path") ?? string.Empty,
                source.TryGetProperty("ordinal", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0,
                otherHeading,
                otherModified,
                Excerpt(otherText));

            // A = newer document (the "likely authoritative" side per §2.13 B).
            var (a, b) = (mine.ModifiedAt ?? DateTimeOffset.MinValue) >= (theirs.ModifiedAt ?? DateTimeOffset.MinValue)
                ? (mine, theirs)
                : (theirs, mine);

            return new ContradictionPair(chunk.Project, a, b, Math.Round(score, 4), DateTimeOffset.MinValue /* stamped at bulk time */);
        }

        return null;
    }

    /// <summary>
    /// The dumb heuristic (§2.13): different source dates and not
    /// near-identical text. Identical text is a copy, not a contradiction;
    /// same-date chunks are siblings of one export, not doc drift.
    /// </summary>
    internal static bool ShouldNominate(string textA, DateTimeOffset? modifiedA, string textB, DateTimeOffset? modifiedB)
    {
        if (modifiedA is null || modifiedB is null || modifiedA == modifiedB)
        {
            return false;
        }

        return !string.Equals(NormalizeForComparison(textA), NormalizeForComparison(textB), StringComparison.Ordinal);
    }

    internal static string NormalizeForComparison(string text)
    {
        var sb = new StringBuilder(text.Length);
        var lastWasSpace = true;
        foreach (var c in text)
        {
            if (char.IsWhiteSpace(c))
            {
                if (!lastWasSpace)
                {
                    sb.Append(' ');
                    lastWasSpace = true;
                }
            }
            else
            {
                sb.Append(char.ToLowerInvariant(c));
                lastWasSpace = false;
            }
        }

        return sb.ToString().TrimEnd();
    }

    internal static string BuildCandidateQuery(float[] vector, string selfPath, string project)
        => JsonSerializer.Serialize(new
        {
            size = CandidatesPerChunk,
            query = new
            {
                knn = new Dictionary<string, object>
                {
                    ["content_vector"] = new
                    {
                        vector,
                        k = CandidatesPerChunk,
                        filter = new
                        {
                            @bool = new
                            {
                                filter = new object[] { new { term = new Dictionary<string, string> { ["project"] = project } } },
                                must_not = new object[] { new { term = new Dictionary<string, string> { ["source_path"] = selfPath } } },
                            },
                        },
                    },
                },
            },
            _source = new[] { "source_path", "ordinal", "heading_path", "content", "source_modified_at" },
        });

    internal static string BuildBulkPayload(IReadOnlyList<ContradictionPair> pairs, DateTimeOffset detectedAt)
    {
        var builder = new StringBuilder();
        foreach (var pair in pairs)
        {
            builder.Append(JsonSerializer.Serialize(new { index = new { _index = ContradictionIndex.Name, _id = pair.Id } })).Append('\n');
            builder.Append(JsonSerializer.Serialize(new
            {
                project = pair.Project,
                similarity = pair.Similarity,
                detected_at = detectedAt.ToUniversalTime(),
                a_path = pair.A.Path,
                a_ordinal = pair.A.Ordinal,
                a_heading = pair.A.Heading,
                a_modified_at = pair.A.ModifiedAt?.ToUniversalTime(),
                a_excerpt = pair.A.Excerpt,
                b_path = pair.B.Path,
                b_ordinal = pair.B.Ordinal,
                b_heading = pair.B.Heading,
                b_modified_at = pair.B.ModifiedAt?.ToUniversalTime(),
                b_excerpt = pair.B.Excerpt,
            })).Append('\n');
        }

        return builder.ToString();
    }

    private static IReadOnlyList<ContradictionPair> ParsePairs(string json)
    {
        using var doc = JsonDocument.Parse(json);
        var pairs = new List<ContradictionPair>();

        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            var s = hit.GetProperty("_source");
            pairs.Add(new ContradictionPair(
                Project: GetString(s, "project") ?? string.Empty,
                A: ParseSide(s, "a"),
                B: ParseSide(s, "b"),
                Similarity: s.TryGetProperty("similarity", out var sim) && sim.ValueKind == JsonValueKind.Number ? sim.GetDouble() : 0,
                DetectedAt: TryGetDate(s, "detected_at") ?? DateTimeOffset.MinValue));
        }

        return pairs;
    }

    private static ContradictionSide ParseSide(JsonElement source, string prefix) => new(
        Path: GetString(source, $"{prefix}_path") ?? string.Empty,
        Ordinal: source.TryGetProperty($"{prefix}_ordinal", out var o) && o.ValueKind == JsonValueKind.Number ? o.GetInt32() : 0,
        Heading: GetString(source, $"{prefix}_heading") ?? string.Empty,
        ModifiedAt: TryGetDate(source, $"{prefix}_modified_at"),
        Excerpt: GetString(source, $"{prefix}_excerpt") ?? string.Empty);

    private static string Excerpt(string text)
    {
        var flat = text.ReplaceLineEndings(" ").Trim();
        return flat.Length <= ExcerptChars ? flat : flat[..ExcerptChars] + "…";
    }

    private static string StripBreadcrumb(string content, string headingPath)
        => content == headingPath
            ? string.Empty
            : content.StartsWith(headingPath + "\n\n", StringComparison.Ordinal)
                ? content[(headingPath.Length + 2)..]
                : content;

    private static string? GetString(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;

    private static DateTimeOffset? TryGetDate(JsonElement source, string name)
        => source.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(value.GetString(), out var parsed)
            ? parsed
            : null;
}
