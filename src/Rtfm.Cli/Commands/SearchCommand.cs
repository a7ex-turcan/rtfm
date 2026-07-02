using Rtfm.Core.OpenSearch;
using Rtfm.Core.Search;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm search &lt;query&gt;</c> — dev aid to verify indexing/retrieval from
/// the CLI (Tier 2 hybrid when the model is available, else Tier 1 BM25).
/// The MCP server exposes the same search.
/// </summary>
internal static class SearchCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        var (queryParts, project) = ParseArgs(args);
        if (queryParts.Count == 0)
        {
            Console.Error.WriteLine("usage: rtfm search <query...> [--project <name>|--all]");
            return 2;
        }

        var query = string.Join(' ', queryParts);

        try
        {
            using var embedder = await EmbedderProvider.TryCreateAsync().ConfigureAwait(false);
            var search = new DocumentSearch(new OpenSearchGateway(), embedder, Console.Error.WriteLine);
            var hits = await search.SearchAsync(query, topK: 5, project: project).ConfigureAwait(false);

            var scope = string.IsNullOrEmpty(project) ? "all projects" : $"project '{project}'";
            Console.Error.WriteLine($"# {hits.Count} hits for \"{query}\" ({scope})");
            foreach (var hit in hits)
            {
                var modified = hit.SourceModifiedAt?.ToString("yyyy-MM-dd") ?? "unknown";
                Console.Out.WriteLine($"── score={hit.Score:F2}  project={hit.Project}  modified={modified}");
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

    /// <summary>Splits query words from flags. Project defaults to null (all projects); --project scopes it.</summary>
    private static (List<string> Query, string? Project) ParseArgs(string[] args)
    {
        var query = new List<string>();
        string? project = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" or "-p" when i + 1 < args.Length:
                    project = args[++i];
                    break;
                case "--all":
                    project = null;
                    break;
                default:
                    query.Add(args[i]);
                    break;
            }
        }

        return (query, project);
    }
}
