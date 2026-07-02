using Rtfm.Core.OpenSearch;
using Rtfm.Core.Search;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm search &lt;query&gt;</c> — dev aid to verify indexing/retrieval from
/// the CLI (Tier 1 BM25). Phase 4 exposes the same search over MCP.
/// </summary>
internal static class SearchCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: rtfm search <query...>");
            return 2;
        }

        var query = string.Join(' ', args);

        try
        {
            var hits = await new DocumentSearch(new OpenSearchGateway()).SearchAsync(query, topK: 5).ConfigureAwait(false);

            Console.Error.WriteLine($"# {hits.Count} hits for \"{query}\"");
            foreach (var hit in hits)
            {
                var modified = hit.SourceModifiedAt?.ToString("yyyy-MM-dd") ?? "unknown";
                Console.Out.WriteLine($"── score={hit.Score:F2}  modified={modified}");
                Console.Out.WriteLine($"   {hit.HeadingPath}");
                Console.Out.WriteLine($"   {Path.GetFileName(hit.SourcePath)}");
                var snippet = hit.Content.Length > 240 ? hit.Content[..240] + "…" : hit.Content;
                Console.Out.WriteLine($"   {snippet.ReplaceLineEndings(" ")}");
                Console.Out.WriteLine();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rtfm search: {ex.Message}");
            return 1;
        }
    }
}
