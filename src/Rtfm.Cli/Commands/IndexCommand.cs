using Rtfm.Core.Chunking;
using Rtfm.Core.Conversion;
using Rtfm.Core.Indexing;
using Rtfm.Core.OpenSearch;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm index &lt;folder&gt;</c> — batch (re)index a documentation folder
/// (§2.4). Converts, chunks, and bulk-upserts each supported file; per-file
/// failures are reported and skipped so one bad doc doesn't sink the run.
/// </summary>
internal static class IndexCommand
{
    private static readonly HashSet<string> SupportedExtensions =
        new(StringComparer.OrdinalIgnoreCase) { ".doc", ".docx", ".md", ".markdown" };

    public static async Task<int> RunAsync(string[] args)
    {
        var (positional, project) = ParseArgs(args);
        if (positional is null)
        {
            Console.Error.WriteLine("usage: rtfm index <folder> [--project <name>]");
            return 2;
        }

        var folder = positional;
        if (!Directory.Exists(folder))
        {
            Console.Error.WriteLine($"rtfm index: folder not found: {folder}");
            return 1;
        }

        var files = Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories)
            .Where(f => SupportedExtensions.Contains(Path.GetExtension(f)))
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();

        if (files.Count == 0)
        {
            Console.Error.WriteLine($"rtfm index: no supported documents (.doc/.docx/.md) under {folder}");
            return 1;
        }

        var converter = new DocumentConverter();
        var chunker = new MarkdownChunker();
        var indexer = new DocumentIndexer(new OpenSearchGateway());

        var created = await indexer.EnsureIndexAsync().ConfigureAwait(false);
        Console.Error.WriteLine(created
            ? $"Created index '{RtfmIndex.Name}'."
            : $"Index '{RtfmIndex.Name}' already exists.");
        Console.Error.WriteLine($"Project: {project}");

        var indexedAt = DateTimeOffset.UtcNow;
        int docCount = 0, chunkCount = 0, failed = 0;

        foreach (var file in files)
        {
            try
            {
                var conversion = converter.Convert(file);
                var sourcePath = PathNormalizer.Normalize(file);
                var modifiedAt = conversion.SourceModifiedAt ?? new DateTimeOffset(File.GetLastWriteTimeUtc(file), TimeSpan.Zero);

                var metadata = new ChunkMetadata(sourcePath, conversion.Title, modifiedAt, project);
                var chunks = chunker.Chunk(conversion.Markdown, metadata);

                var n = await indexer.IndexDocumentAsync(chunks, indexedAt).ConfigureAwait(false);
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

        await indexer.RefreshAsync().ConfigureAwait(false);

        Console.Error.WriteLine($"Done: {docCount} docs, {chunkCount} chunks indexed into '{RtfmIndex.Name}' (project '{project}')"
            + (failed > 0 ? $", {failed} failed." : "."));
        return failed > 0 && docCount == 0 ? 1 : 0;
    }

    /// <summary>Returns the folder (null if missing) and the project name (default "default").</summary>
    private static (string? Folder, string Project) ParseArgs(string[] args)
    {
        string? folder = null;
        var project = "default";

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--project" or "-p")
            {
                if (i + 1 >= args.Length)
                {
                    return (null, project);
                }

                project = args[++i];
            }
            else
            {
                folder ??= args[i];
            }
        }

        return (folder, project);
    }
}
