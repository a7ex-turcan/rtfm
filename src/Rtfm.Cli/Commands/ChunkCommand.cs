using Rtfm.Core.Chunking;
using Rtfm.Core.Conversion;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm chunk &lt;path&gt;</c> — dev aid: convert a document and print its
/// heading-aware chunks (ordinal, breadcrumb, size, snippet) so chunking can be
/// eyeballed while building Phase 2.
/// </summary>
internal static class ChunkCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: rtfm chunk <path>");
            return 2;
        }

        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"rtfm chunk: file not found: {path}");
            return 1;
        }

        try
        {
            var conversion = new DocumentConverter().Convert(path);
            var metadata = new ChunkMetadata(
                SourcePath: path,
                DocumentTitle: conversion.Title,
                SourceModifiedAt: new DateTimeOffset(File.GetLastWriteTimeUtc(path), TimeSpan.Zero));

            var chunks = new MarkdownChunker().Chunk(conversion.Markdown, metadata);

            Console.Error.WriteLine(
                $"# {chunks.Count} chunks from {conversion.Format} ({conversion.Markdown.Length} chars markdown)");

            foreach (var chunk in chunks)
            {
                Console.Out.WriteLine($"── [{chunk.Ordinal}] {chunk.HeadingPath}  ({chunk.Text.Length} chars)");
                var snippet = chunk.Text.Length > 200 ? chunk.Text[..200] + "…" : chunk.Text;
                if (snippet.Length > 0)
                {
                    Console.Out.WriteLine(snippet);
                }

                Console.Out.WriteLine();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rtfm chunk: {ex.Message}");
            return 1;
        }
    }
}
