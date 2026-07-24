using System.Text.Json;
using Rtfm.Core.Indexing;
using Rtfm.Core.Jira;
using Rtfm.Core.Manifest;
using Rtfm.Core.OpenSearch;
using Spectre.Console;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm purge &lt;project&gt;</c> — removes everything RTFM holds for one
/// project: its chunks in OpenSearch (delete-by-query on the <c>project</c>
/// keyword, §2.9/§2.14) *and* its watch manifests (else the next
/// <c>rtfm watch</c> would reconcile against a stale baseline). Destructive, so
/// it shows what it's about to delete and asks — <c>--yes</c> skips the prompt
/// (required when output is redirected, where no prompt is possible).
/// </summary>
internal static class PurgeCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        string? project = null;
        var yes = false;

        foreach (var arg in args)
        {
            if (arg is "--yes" or "-y")
            {
                yes = true;
            }
            else
            {
                project ??= arg;
            }
        }

        if (string.IsNullOrWhiteSpace(project))
        {
            Console.Error.WriteLine("usage: rtfm purge <project> [--yes]");
            return 2;
        }

        var gateway = new OpenSearchGateway();

        // Show what's on the block before asking.
        var (chunks, docs) = await CountProjectAsync(gateway, project).ConfigureAwait(false);
        var manifests = ManifestStore.FindManifests(project).Count;
        var jiraConfigured = JiraConfigStore.Load(project) is not null;
        var jiraMonitored = JiraMonitorStore.Load(project).Count;

        if (chunks == 0 && manifests == 0 && !jiraConfigured && jiraMonitored == 0)
        {
            Ui.Err.MarkupLine($"Nothing indexed under project [{Ui.Accent}]{Ui.E(project)}[/] — nothing to purge.");
            return 0;
        }

        Ui.Err.MarkupLine(
            $"Project [{Ui.Accent}]{Ui.E(project)}[/]: [bold]{chunks}[/] chunks across [bold]{docs}[/] docs, "
            + $"[bold]{manifests}[/] watch manifest{(manifests == 1 ? "" : "s")}"
            + (jiraConfigured || jiraMonitored > 0 ? $", [bold]Jira[/] config{(jiraMonitored > 0 ? $" + {jiraMonitored} monitored ticket(s)" : "")}" : "")
            + ".");

        if (!yes)
        {
            if (!Ui.Fancy)
            {
                Console.Error.WriteLine("rtfm purge: refusing to delete without --yes when not running interactively.");
                return 2;
            }

            if (!Ui.Err.Confirm($"[red]Delete all of it?[/]", defaultValue: false))
            {
                Console.Error.WriteLine("Aborted.");
                return 1;
            }
        }

        long deleted = 0;
        if (chunks > 0)
        {
            deleted = await gateway.DeleteByTermAsync(RtfmIndex.Name, "project", project).ConfigureAwait(false);
            await gateway.RefreshAsync(RtfmIndex.Name).ConfigureAwait(false);
        }

        var removedManifests = ManifestStore.PurgeManifests(project);
        var removedPairs = await new Rtfm.Core.Contradictions.ContradictionDetector(gateway).PurgeProjectAsync(project).ConfigureAwait(false);
        var removedNotes = await new Rtfm.Core.Notes.NotesStore(gateway).PurgeProjectAsync(project).ConfigureAwait(false);

        // Jira chunks carry the project keyword, so they were already deleted
        // above; drop the connector config + monitored set so nothing lingers.
        var removedJiraConfig = JiraConfigStore.Remove(project);
        var removedJiraMonitor = JiraMonitorStore.Remove(project);

        Ui.Err.MarkupLine(
            $"[green]Purged[/] project [{Ui.Accent}]{Ui.E(project)}[/]: "
            + $"[bold]{deleted}[/] chunks deleted, [bold]{removedManifests}[/] manifest{(removedManifests == 1 ? "" : "s")} removed, "
            + $"[bold]{removedPairs}[/] contradiction pair{(removedPairs == 1 ? "" : "s")} dropped, "
            + $"[bold]{removedNotes}[/] note{(removedNotes == 1 ? "" : "s")} removed"
            + (removedJiraConfig || removedJiraMonitor ? ", [bold]Jira[/] config + monitor removed" : "")
            + ".");
        return 0;
    }

    /// <summary>Chunk count + distinct-doc count for the project (0/0 when the index doesn't exist yet).</summary>
    private static async Task<(long Chunks, long Docs)> CountProjectAsync(OpenSearchGateway gateway, string project)
    {
        if (!await gateway.IndexExistsAsync(RtfmIndex.Name).ConfigureAwait(false))
        {
            return (0, 0);
        }

        var body = JsonSerializer.Serialize(new
        {
            size = 0,
            query = new { term = new Dictionary<string, string> { ["project"] = project } },
            aggs = new { docs = new { cardinality = new { field = "source_path" } } },
        });

        var json = await gateway.SearchAsync(RtfmIndex.Name, body).ConfigureAwait(false);
        using var doc = JsonDocument.Parse(json);

        var chunks = doc.RootElement.GetProperty("hits").GetProperty("total").GetProperty("value").GetInt64();
        var docs = doc.RootElement.TryGetProperty("aggregations", out var aggs)
            ? aggs.GetProperty("docs").GetProperty("value").GetInt64()
            : 0;
        return (chunks, docs);
    }
}
