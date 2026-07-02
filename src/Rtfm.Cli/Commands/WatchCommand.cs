using Rtfm.Core.Indexing;
using Rtfm.Core.Manifest;
using Rtfm.Core.OpenSearch;
using Rtfm.Core.Watch;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm watch &lt;folder&gt;</c> — long-running incremental indexer (§2.8,
/// Phase 5). Reconciles against the manifest on start, then keeps the index
/// fresh on change until Ctrl+C. All output goes to stderr.
/// </summary>
internal static class WatchCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (folder, project) = CommandArgs.ParseFolderAndProject(args);
        if (folder is null)
        {
            Console.Error.WriteLine("usage: rtfm watch <folder> [--project <name>]");
            return 2;
        }

        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"rtfm watch: folder not found: {folder}");
            return 1;
        }

        var indexer = new DocumentIndexer(new OpenSearchGateway());
        var ingestor = new DocumentIngestor(indexer);
        var manifestStore = ManifestStore.For(folder, project);
        var watcher = new FolderWatcher(folder, project, ingestor, manifestStore, Console.Error.WriteLine);

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) =>
        {
            e.Cancel = true; // let us shut down cleanly instead of hard-killing
            cts.Cancel();
        };

        try
        {
            await watcher.RunAsync(cts.Token).ConfigureAwait(false);
            return 0;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("Stopped.");
            return 0;
        }
    }
}
