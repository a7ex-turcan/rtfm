using System.Security.Cryptography;
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

    /// <summary>
    /// A chunk whose normalized text appears in this many (or more) documents
    /// of one project is template boilerplate by definition (Phase 22 — PRD
    /// headers, shared "Document Owners" tables) and never nominated. The same
    /// threshold applies per line for template *variants* (copies that differ
    /// by a few characters, so the whole-chunk hash can't see them).
    /// </summary>
    internal const int TemplateDocThreshold = 3;

    /// <summary>
    /// Line-level template suppression needs at least this many shared
    /// template lines before it fires — one stray boilerplate row shared by
    /// two otherwise-substantive chunks must not kill the nomination.
    /// </summary>
    internal const int MinSharedTemplateLines = 3;

    /// <summary>
    /// Normalized lines shorter than this carry no template signal (markdown
    /// separators, `---` rows) and are skipped when hashing lines.
    /// </summary>
    internal const int MinLineChars = 16;

    /// <summary>
    /// Date gap at which a disagreement reads as likely supersession rather
    /// than a peer contradiction (Phase 22): a month+ of drift means the older
    /// doc probably went stale, not that two live docs disagree.
    /// </summary>
    internal static readonly TimeSpan SupersessionGap = TimeSpan.FromDays(30);

    private const int CandidatesPerChunk = 3;
    private const int ExcerptChars = 240;

    private static readonly string[] ClosedStatuses = [ContradictionPair.StatusDismissed, ContradictionPair.StatusResolved];

    /// <summary>
    /// Creates the side index if missing; on an existing index, PUTs the
    /// Phase 22 lifecycle fields into the mapping (idempotent). Returns true
    /// if created.
    /// </summary>
    public async Task<bool> EnsureIndexAsync(CancellationToken cancellationToken = default)
    {
        var created = await gateway.EnsureIndexAsync(ContradictionIndex.Name, ContradictionIndex.DefinitionJson, cancellationToken).ConfigureAwait(false);
        if (!created)
        {
            await gateway.PutMappingAsync(ContradictionIndex.Name, ContradictionIndex.MappingAdditionsJson, cancellationToken).ConfigureAwait(false);
        }

        return created;
    }

    /// <summary>
    /// Drops pairs referencing <paramref name="normalizedPath"/> (either side).
    /// With <paramref name="onlyOpen"/> (the re-ingest path) dismissed/resolved
    /// pairs are preserved so a human verdict survives re-indexing — without it
    /// every dismissal would resurrect on the next <c>rtfm index</c>. Full
    /// removal (document deleted) drops everything.
    /// </summary>
    public Task<long> RemoveForPathAsync(string normalizedPath, bool onlyOpen = false, CancellationToken cancellationToken = default)
        => gateway.DeleteByQueryAsync(ContradictionIndex.Name, BuildRemoveQuery(normalizedPath, onlyOpen), cancellationToken);

    internal static string BuildRemoveQuery(string normalizedPath, bool onlyOpen)
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
                    // Absent status (pre-Phase 22 pairs) reads as open, so a
                    // must_not on the closed statuses keeps legacy pairs eligible.
                    must_not = onlyOpen
                        ? new object[] { new { terms = new Dictionary<string, string[]> { ["status"] = ClosedStatuses } } }
                        : [],
                },
            },
        };

        return JsonSerializer.Serialize(query);
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

        var nominations = new List<Nomination>();

        for (var i = 0; i < chunks.Count; i++)
        {
            var chunk = chunks[i];
            var body = BuildCandidateQuery(vectors[i], chunk.SourcePath, chunk.Project);
            var json = await gateway.SearchAsync(RtfmIndex.Name, body, cancellationToken: cancellationToken).ConfigureAwait(false);

            if (EvaluateCandidates(chunk, json) is { } nomination)
            {
                nominations.Add(nomination);
            }
        }

        if (nominations.Count == 0)
        {
            return 0;
        }

        // Template suppression (Phase 22): text shared verbatim by 3+ docs of
        // the project is boilerplate, not knowledge — drop nominations where
        // either side is template text. One aggregation for all hashes.
        var templateHashes = await GetTemplateHashesAsync(chunks[0].Project, nominations, cancellationToken).ConfigureAwait(false);
        var survivors = nominations
            .Where(n => !templateHashes.Contains(n.MineHash) && !templateHashes.Contains(n.OtherHash))
            .ToList();

        // Line-level pass for template *variants*: copies that differ by a few
        // characters evade the whole-chunk hash (a real pair: two PRD-header
        // chunks sharing a "Document Owners" table that a third doc also
        // carries, each copy trivially different). When the verbatim lines a
        // pair's sides share are mostly corpus-wide template lines, the
        // similarity came from boilerplate — suppress.
        if (survivors.Count > 0)
        {
            var templateLines = await GetTemplateLineHashesAsync(chunks[0].Project, survivors, cancellationToken).ConfigureAwait(false);
            survivors = survivors.Where(n => !IsTemplateOverlap(n.SharedLineHashes, templateLines)).ToList();
        }

        var pairs = survivors.Select(n => n.Pair).ToList();

        // Closed pairs stay closed (Phase 22): a dismissed/resolved pair keeps
        // its deterministic id, so skip re-nominating it as open.
        if (pairs.Count > 0)
        {
            var closed = await GetClosedIdsAsync(pairs.Select(p => p.Id).Distinct().ToList(), cancellationToken).ConfigureAwait(false);
            pairs = pairs.Where(p => !closed.Contains(p.Id)).ToList();
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

    /// <summary>Hashes among the nominations' sides whose text spans ≥ <see cref="TemplateDocThreshold"/> documents in the project.</summary>
    private async Task<IReadOnlySet<string>> GetTemplateHashesAsync(
        string project, IReadOnlyList<Nomination> nominations, CancellationToken cancellationToken)
    {
        var hashes = nominations.SelectMany(n => new[] { n.MineHash, n.OtherHash }).Distinct().ToList();
        var json = await gateway.SearchAsync(RtfmIndex.Name, BuildTemplateCountQuery(project, "content_hash", hashes), cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseTemplateHashes(json, TemplateDocThreshold);
    }

    /// <summary>Line hashes among the nominations' shared lines that span ≥ <see cref="TemplateDocThreshold"/> documents in the project.</summary>
    private async Task<IReadOnlySet<string>> GetTemplateLineHashesAsync(
        string project, IReadOnlyList<Nomination> nominations, CancellationToken cancellationToken)
    {
        var hashes = nominations.SelectMany(n => n.SharedLineHashes).Distinct().ToList();
        if (hashes.Count == 0)
        {
            return new HashSet<string>();
        }

        var json = await gateway.SearchAsync(RtfmIndex.Name, BuildTemplateCountQuery(project, "line_hashes", hashes), cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseTemplateHashes(json, TemplateDocThreshold);
    }

    /// <summary>
    /// True when the pair's verbatim overlap is dominated by corpus-wide
    /// template lines: at least <see cref="MinSharedTemplateLines"/> shared
    /// lines are template, and they are the majority of everything shared.
    /// </summary>
    internal static bool IsTemplateOverlap(IReadOnlyList<string> sharedLineHashes, IReadOnlySet<string> templateLineHashes)
    {
        var templateCount = sharedLineHashes.Count(templateLineHashes.Contains);
        return templateCount >= MinSharedTemplateLines && templateCount * 2 >= sharedLineHashes.Count;
    }

    /// <summary>Ids among <paramref name="pairIds"/> whose stored pair is dismissed or resolved.</summary>
    private async Task<IReadOnlySet<string>> GetClosedIdsAsync(IReadOnlyList<string> pairIds, CancellationToken cancellationToken)
    {
        if (!await gateway.IndexExistsAsync(ContradictionIndex.Name, cancellationToken).ConfigureAwait(false))
        {
            return new HashSet<string>();
        }

        var body = JsonSerializer.Serialize(new
        {
            size = pairIds.Count,
            query = new
            {
                @bool = new
                {
                    filter = new object[]
                    {
                        new { ids = new { values = pairIds } },
                        new { terms = new Dictionary<string, string[]> { ["status"] = ClosedStatuses } },
                    },
                },
            },
            _source = false,
        });

        var json = await gateway.SearchAsync(ContradictionIndex.Name, body, cancellationToken: cancellationToken).ConfigureAwait(false);

        using var doc = JsonDocument.Parse(json);
        var closed = new HashSet<string>(StringComparer.Ordinal);
        foreach (var hit in doc.RootElement.GetProperty("hits").GetProperty("hits").EnumerateArray())
        {
            if (hit.TryGetProperty("_id", out var id) && id.ValueKind == JsonValueKind.String)
            {
                closed.Add(id.GetString()!);
            }
        }

        return closed;
    }

    /// <summary>One pair by its id, any status. Null when it does not exist.</summary>
    public async Task<ContradictionPair?> GetAsync(string id, CancellationToken cancellationToken = default)
    {
        if (!await gateway.IndexExistsAsync(ContradictionIndex.Name, cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var body = JsonSerializer.Serialize(new { size = 1, query = new { ids = new { values = new[] { id } } } });
        var json = await gateway.SearchAsync(ContradictionIndex.Name, body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParsePairs(json).FirstOrDefault();
    }

    /// <summary>
    /// Marks a pair dismissed or resolved (Phase 22). The verdict is a human
    /// judgment — callers own the confirmation; this just records it. Closed
    /// pairs survive re-ingest of either document and are never re-nominated.
    /// </summary>
    public async Task SetStatusAsync(string id, string status, string? resolvedNoteId = null, CancellationToken cancellationToken = default)
    {
        var partial = JsonSerializer.Serialize(new
        {
            status,
            resolved_note_id = resolvedNoteId,
            status_changed_at = DateTimeOffset.UtcNow,
        }, PartialJson);

        await gateway.UpdateAsync(ContradictionIndex.Name, id, partial, cancellationToken).ConfigureAwait(false);
        await gateway.RefreshAsync(ContradictionIndex.Name, cancellationToken).ConfigureAwait(false);
    }

    private static readonly JsonSerializerOptions PartialJson = new()
    {
        DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull,
    };

    /// <summary>
    /// Nominated pairs, newest detection first, optionally scoped to one
    /// project. Open pairs only by default (Phase 22);
    /// <paramref name="includeClosed"/> adds dismissed/resolved history.
    /// </summary>
    public async Task<IReadOnlyList<ContradictionPair>> ListAsync(
        string? project = null, int topK = 50, bool includeClosed = false, CancellationToken cancellationToken = default)
    {
        if (!await gateway.IndexExistsAsync(ContradictionIndex.Name, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        var filters = new List<object>();
        if (!string.IsNullOrWhiteSpace(project))
        {
            filters.Add(new { term = new Dictionary<string, string> { ["project"] = project } });
        }

        var body = JsonSerializer.Serialize(new
        {
            size = topK,
            query = new
            {
                @bool = new
                {
                    filter = filters,
                    // Missing status (pre-Phase 22 pairs) counts as open.
                    must_not = includeClosed
                        ? []
                        : new object[] { new { terms = new Dictionary<string, string[]> { ["status"] = ClosedStatuses } } },
                },
            },
            sort = new object[] { new { detected_at = "desc" }, new { similarity = "desc" } },
        });

        var json = await gateway.SearchAsync(ContradictionIndex.Name, body, cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParsePairs(json);
    }

    // ---- pure logic (internal for tests) ----

    /// <summary>
    /// A tentative pair plus both sides' content hashes and the hashes of the
    /// lines the sides share verbatim (for the two template-suppression passes).
    /// </summary>
    internal sealed record Nomination(ContradictionPair Pair, string MineHash, string OtherHash, IReadOnlyList<string> SharedLineHashes);

    /// <summary>Picks the best qualifying candidate for one chunk, or null.</summary>
    internal static Nomination? EvaluateCandidates(Chunk chunk, string searchJson)
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

            // Phase 22: a month+ of drift reads as supersession-to-confirm,
            // not a peer contradiction. Dates are non-null past ShouldNominate.
            var gap = (chunk.SourceModifiedAt!.Value - otherModified!.Value).Duration();
            var kind = gap >= SupersessionGap ? ContradictionPair.KindSupersession : ContradictionPair.KindContradiction;

            var pair = new ContradictionPair(chunk.Project, a, b, Math.Round(score, 4), DateTimeOffset.MinValue /* stamped at bulk time */, kind);
            var shared = LineHashes(chunk.Text).Intersect(LineHashes(otherText), StringComparer.Ordinal).ToList();
            return new Nomination(pair, ContentHash(chunk.Text), ContentHash(otherText), shared);
        }

        return null;
    }

    /// <summary>
    /// Fingerprint of a chunk's normalized text (Phase 22): stamped on every
    /// chunk at ingest as <c>content_hash</c> so "how many docs share this
    /// exact text" is one aggregation. Same normalization as
    /// <see cref="ShouldNominate"/>'s copy check.
    /// </summary>
    public static string ContentHash(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(NormalizeForComparison(text))))[..16].ToLowerInvariant();

    /// <summary>
    /// Hashes of a chunk's qualifying normalized lines (Phase 22): stamped at
    /// ingest as <c>line_hashes</c> so template *variants* — copies of a
    /// boilerplate table that differ by a few characters — are countable line
    /// by line where the whole-chunk hash can't match. Lines shorter than
    /// <see cref="MinLineChars"/> after normalization (separator rows, `---`)
    /// carry no signal and are skipped; hashes are deduplicated.
    /// </summary>
    public static IReadOnlyList<string> LineHashes(string text)
    {
        var hashes = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var line in text.Split('\n'))
        {
            var normalized = NormalizeForComparison(line);
            if (normalized.Length >= MinLineChars
                && Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(normalized)))[..16].ToLowerInvariant() is { } hash
                && seen.Add(hash))
            {
                hashes.Add(hash);
            }
        }

        return hashes;
    }

    internal static string BuildTemplateCountQuery(string project, string field, IReadOnlyList<string> hashes)
        => JsonSerializer.Serialize(new
        {
            size = 0,
            query = new
            {
                @bool = new
                {
                    filter = new object[]
                    {
                        new { term = new Dictionary<string, string> { ["project"] = project } },
                        new { terms = new Dictionary<string, object> { [field] = hashes } },
                    },
                },
            },
            aggs = new
            {
                by_hash = new
                {
                    terms = new { field, size = Math.Max(hashes.Count, 1) },
                    aggs = new { docs = new { cardinality = new { field = "source_path" } } },
                },
            },
        });

    internal static IReadOnlySet<string> ParseTemplateHashes(string json, int threshold)
    {
        using var doc = JsonDocument.Parse(json);
        var hashes = new HashSet<string>(StringComparer.Ordinal);

        if (!doc.RootElement.TryGetProperty("aggregations", out var aggs))
        {
            return hashes;
        }

        foreach (var bucket in aggs.GetProperty("by_hash").GetProperty("buckets").EnumerateArray())
        {
            if (bucket.GetProperty("docs").GetProperty("value").GetInt64() >= threshold
                && bucket.GetProperty("key").GetString() is { } key)
            {
                hashes.Add(key);
            }
        }

        return hashes;
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
                kind = pair.Kind,
                status = pair.Status,
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
                DetectedAt: TryGetDate(s, "detected_at") ?? DateTimeOffset.MinValue,
                Kind: GetString(s, "kind") ?? ContradictionPair.KindContradiction,
                Status: GetString(s, "status") ?? ContradictionPair.StatusOpen,
                ResolvedNoteId: GetString(s, "resolved_note_id")));
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
