using System.Text.Json;
using Rtfm.Core.Confluence;
using Rtfm.Core.Contradictions;
using Rtfm.Core.Database;
using Rtfm.Core.Indexing;
using Rtfm.Core.Jira;
using Rtfm.Core.Manifest;
using Rtfm.Core.Notes;
using Rtfm.Core.OpenSearch;

namespace Rtfm.Core.Search;

/// <summary>How many documents/chunks a project holds of one source kind (`pdf`, `sql`, `jira`, …).</summary>
public sealed record SourceKindCount(string Kind, int Documents, long Chunks);

/// <summary>Contradiction rollup for one project: open (with the supersession share) versus closed.</summary>
public sealed record ContradictionSummary(int Open, int LikelySupersession, int Dismissed, int Resolved)
{
    public static readonly ContradictionSummary Empty = new(0, 0, 0, 0);

    public int Total => Open + Dismissed + Resolved;
}

/// <summary>A configured API-pull connector (§2.16 Jira / §2.17 Confluence) and its monitored set.</summary>
public sealed record ConnectorSummary(
    string Kind,
    string BaseUrl,
    string Email,
    string TokenReference,
    int Monitored,
    DateTimeOffset? LastPolledAt,
    int PollSeconds);

/// <summary>
/// Everything RTFM holds for one project, in one object — the read-side mirror
/// of what <c>rtfm purge</c> would delete. Index-backed facts
/// (<see cref="Status"/>, <see cref="Documents"/>, <see cref="Notes"/>,
/// <see cref="Contradictions"/>) plus the local state that lives outside
/// OpenSearch (watch manifests, `.rtfmdb` connectors, Jira/Confluence configs).
/// </summary>
public sealed record ProjectDescription(
    string Name,
    ProjectStatus? Status,
    IReadOnlyList<SourceKindCount> Kinds,
    IReadOnlyList<SourceInfo> Documents,
    IReadOnlyList<ManifestInfo> Manifests,
    IReadOnlyList<DatabaseInfo> Databases,
    IReadOnlyList<Note> Notes,
    ContradictionSummary Contradictions,
    IReadOnlyList<ContradictionPair> OpenPairs,
    ConnectorSummary? Jira,
    ConnectorSummary? Confluence)
{
    /// <summary>
    /// False when RTFM has never heard of this project — nothing indexed, no
    /// manifest, no connector, no note. Lets a caller say "not found" instead of
    /// rendering an all-zeroes report for a typo.
    /// </summary>
    public bool Exists =>
        Status is not null
        || Documents.Count > 0
        || Manifests.Count > 0
        || Databases.Count > 0
        || Notes.Count > 0
        || Contradictions.Total > 0
        || Jira is not null
        || Confluence is not null;
}

/// <summary>
/// Assembles a <see cref="ProjectDescription"/> — the detail view behind
/// <c>rtfm describe project &lt;name&gt;</c>. Everything here is a read: the
/// Phase 10 status aggregation, the Phase 8 catalog, the notes and
/// contradiction side indexes, and the on-disk state stores. No new fields, no
/// ingest changes.
/// </summary>
public sealed class ProjectDescriber(OpenSearchGateway gateway)
{
    /// <summary>Open pairs listed in full (beyond this, only the counts are reported).</summary>
    public const int MaxOpenPairsListed = 5;

    public async Task<ProjectDescription> DescribeAsync(string project, CancellationToken cancellationToken = default)
    {
        var indexed = await gateway.IndexExistsAsync(RtfmIndex.Name, cancellationToken).ConfigureAwait(false);

        ProjectStatus? status = null;
        IReadOnlyList<SourceInfo> documents = [];
        if (indexed)
        {
            var statuses = await new StatusService(gateway).GetProjectStatusesAsync(project, cancellationToken).ConfigureAwait(false);
            status = statuses.FirstOrDefault(s => string.Equals(s.Project, project, StringComparison.Ordinal));
            documents = await new DocumentCatalog(gateway).ListSourcesAsync(project, cancellationToken).ConfigureAwait(false);
        }

        var detector = new ContradictionDetector(gateway);
        var contradictions = await SummarizeContradictionsAsync(project, cancellationToken).ConfigureAwait(false);
        var openPairs = contradictions.Open > 0
            ? await detector.ListAsync(project, MaxOpenPairsListed, includeClosed: false, cancellationToken).ConfigureAwait(false)
            : [];

        var notes = await new NotesStore(gateway).ListAsync(project, cancellationToken: cancellationToken).ConfigureAwait(false);

        return new ProjectDescription(
            Name: project,
            Status: status,
            Kinds: SummarizeKinds(documents),
            Documents: documents,
            Manifests: ManifestStore.ListAll().Where(m => string.Equals(m.Project, project, StringComparison.Ordinal)).ToList(),
            Databases: DatabaseRegistry.List(project),
            Notes: notes,
            Contradictions: contradictions,
            OpenPairs: openPairs,
            Jira: DescribeJira(project),
            Confluence: DescribeConfluence(project));
    }

    private async Task<ContradictionSummary> SummarizeContradictionsAsync(string project, CancellationToken cancellationToken)
    {
        if (!await gateway.IndexExistsAsync(ContradictionIndex.Name, cancellationToken).ConfigureAwait(false))
        {
            return ContradictionSummary.Empty;
        }

        var json = await gateway.SearchAsync(
            ContradictionIndex.Name, BuildContradictionSummaryQuery(project), cancellationToken: cancellationToken).ConfigureAwait(false);
        return ParseContradictionSummary(json);
    }

    private static ConnectorSummary? DescribeJira(string project)
    {
        if (JiraConfigStore.Load(project) is not { } config)
        {
            return null;
        }

        var monitor = JiraMonitorStore.Load(project);
        return new ConnectorSummary("Jira", config.BaseUrl, config.Email, config.Token, monitor.Count, monitor.LastPolledAt, config.PollSeconds);
    }

    private static ConnectorSummary? DescribeConfluence(string project)
    {
        if (ConfluenceConfigStore.Load(project) is not { } config)
        {
            return null;
        }

        var monitor = ConfluenceMonitorStore.Load(project);
        return new ConnectorSummary("Confluence", config.BaseUrl, config.Email, config.Token, monitor.Count, monitor.LastPolledAt, config.PollSeconds);
    }

    // ---- pure logic (internal for tests) ----

    /// <summary>
    /// Groups documents by source kind, biggest first. Synthetic keys
    /// (<c>jira://</c>, <c>confluence://</c>) name their product — they have no
    /// extension and must never see <see cref="Path"/> handling (§2.16);
    /// everything else is keyed by its file extension.
    /// </summary>
    internal static IReadOnlyList<SourceKindCount> SummarizeKinds(IReadOnlyList<SourceInfo> documents)
        => documents
            .GroupBy(d => ClassifyKind(d.Path), StringComparer.Ordinal)
            .Select(g => new SourceKindCount(g.Key, g.Count(), g.Sum(d => d.ChunkCount)))
            .OrderByDescending(k => k.Documents)
            .ThenBy(k => k.Kind, StringComparer.Ordinal)
            .ToList();

    internal static string ClassifyKind(string sourcePath)
    {
        if (sourcePath.StartsWith("jira://", StringComparison.OrdinalIgnoreCase))
        {
            return "jira";
        }

        if (sourcePath.StartsWith("confluence://", StringComparison.OrdinalIgnoreCase))
        {
            return "confluence";
        }

        var extension = Path.GetExtension(sourcePath).TrimStart('.').ToLowerInvariant();
        return extension.Length == 0 ? "(no extension)" : extension;
    }

    /// <summary>
    /// Status/kind counts in one pass. Pre-Phase-22 pairs carry neither field,
    /// so both terms aggs declare the documented default as <c>missing</c>
    /// (absent status reads as open, absent kind as a peer contradiction).
    /// </summary>
    internal static string BuildContradictionSummaryQuery(string project)
        => JsonSerializer.Serialize(new
        {
            size = 0,
            query = new { term = new Dictionary<string, string> { ["project"] = project } },
            aggs = new
            {
                statuses = new
                {
                    terms = new { field = "status", size = 10, missing = ContradictionPair.StatusOpen },
                },
                open_kinds = new
                {
                    filter = new
                    {
                        @bool = new
                        {
                            must_not = new object[]
                            {
                                new
                                {
                                    terms = new Dictionary<string, string[]>
                                    {
                                        ["status"] = [ContradictionPair.StatusDismissed, ContradictionPair.StatusResolved],
                                    },
                                },
                            },
                        },
                    },
                    aggs = new
                    {
                        kinds = new { terms = new { field = "kind", size = 10, missing = ContradictionPair.KindContradiction } },
                    },
                },
            },
        });

    internal static ContradictionSummary ParseContradictionSummary(string json)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("aggregations", out var aggs))
        {
            return ContradictionSummary.Empty;
        }

        var byStatus = Buckets(aggs.GetProperty("statuses"));
        var supersessions = aggs.TryGetProperty("open_kinds", out var openKinds)
            ? Buckets(openKinds.GetProperty("kinds")).GetValueOrDefault(ContradictionPair.KindSupersession)
            : 0;

        return new ContradictionSummary(
            Open: byStatus.GetValueOrDefault(ContradictionPair.StatusOpen),
            LikelySupersession: supersessions,
            Dismissed: byStatus.GetValueOrDefault(ContradictionPair.StatusDismissed),
            Resolved: byStatus.GetValueOrDefault(ContradictionPair.StatusResolved));
    }

    private static Dictionary<string, int> Buckets(JsonElement terms)
    {
        var counts = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var bucket in terms.GetProperty("buckets").EnumerateArray())
        {
            counts[bucket.GetProperty("key").GetString() ?? string.Empty] = bucket.GetProperty("doc_count").GetInt32();
        }

        return counts;
    }
}
