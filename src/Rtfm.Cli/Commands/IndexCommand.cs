using Rtfm.Core.Indexing;
using Rtfm.Core.Manifest;
using Rtfm.Core.OpenSearch;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm index &lt;folder&gt;</c> — batch (re)index a documentation folder
/// (§2.4). Converts, chunks, and bulk-upserts each supported file via the shared
/// <see cref="DocumentIngestor"/>; per-file failures are reported and skipped so
/// one bad doc doesn't sink the run. Writes a manifest of what it indexed so
/// <c>rtfm watch</c>'s startup reconcile (§2.8) starts from a correct baseline.
/// </summary>
internal static class IndexCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (folder, project) = CommandArgs.ParseFolderAndProject(args);
        if (folder is null)
        {
            Console.Error.WriteLine("usage: rtfm index <folder> [--project <name>]");
            return 2;
        }

        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"rtfm index: folder not found: {folder}");
            return 1;
        }

        var files = DocumentIngestor.EnumerateSupportedFiles(folder).ToList();
        if (files.Count == 0)
        {
            Console.Error.WriteLine($"rtfm index: no supported documents (.doc/.docx/.md) under {folder}");
            return 1;
        }

        using var embedder = await EmbedderProvider.TryCreateAsync().ConfigureAwait(false);
        var indexer = new DocumentIndexer(new OpenSearchGateway());
        var ingestor = new DocumentIngestor(indexer, embedder);

        var created = await ingestor.EnsureIndexAsync().ConfigureAwait(false);
        Console.Error.WriteLine(created
            ? $"Created index '{RtfmIndex.Name}'."
            : $"Index '{RtfmIndex.Name}' already exists.");
        Console.Error.WriteLine($"Project: {project}");

        var manifestStore = ManifestStore.For(folder, project);
        var manifest = new DocumentManifest();

        var indexedAt = DateTimeOffset.UtcNow;
        int docCount = 0, chunkCount = 0, failed = 0;

        foreach (var file in files)
        {
            try
            {
                var n = await ingestor.IngestFileAsync(file, project, indexedAt).ConfigureAwait(false);
                var info = new FileInfo(file);
                manifest.Set(PathNormalizer.Normalize(file), new ManifestEntry(info.LastWriteTimeUtc.Ticks, info.Length));
                docCount++;
                chunkCount += n;
                Console.Error.WriteLine($"  indexed {Path.GetFileName(file)} → {n} chunks");
            }
            catch (Exception ex)
            {
                failed++;
                Console.Error.WriteLine($"  FAILED {Path.GetFileName(file)}: {ex.Message}");
            }
        }

        await ingestor.RefreshAsync().ConfigureAwait(false);
        manifestStore.Save(manifest);

        Console.Error.WriteLine($"Done: {docCount} docs, {chunkCount} chunks indexed into '{RtfmIndex.Name}' (project '{project}')"
            + (failed > 0 ? $", {failed} failed." : "."));
        return failed > 0 && docCount == 0 ? 1 : 0;
    }
}
