using Rtfm.Core.OpenSearch;
using Rtfm.Core.Search;
using Spectre.Console;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm search &lt;query&gt;</c> — dev aid to verify indexing/retrieval from
/// the CLI (Tier 2 hybrid when the model is available, else Tier 1 BM25).
/// The MCP server exposes the same search. Phase 7: hits render as ranked
/// cards (score bar, breadcrumb, source) on stdout.
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
            using var reranker = await EmbedderProvider.TryCreateRerankerAsync().ConfigureAwait(false);
            var search = new DocumentSearch(new OpenSearchGateway(), embedder, Console.Error.WriteLine, reranker);

            var hits = await Ui.Err.Status()
                .Spinner(Spinner.Known.Dots)
                .StartAsync("Searching…", _ => search.SearchAsync(query, topK: 5, project: project));

            var scope = string.IsNullOrEmpty(project) ? "all projects" : $"project '{project}'";
            Ui.Err.MarkupLine($"[bold]{hits.Count}[/] hits for [italic]\"{Ui.E(query)}\"[/] [dim]({Ui.E(scope)})[/]"
                + (embedder is null ? " [yellow](lexical-only)[/]" : string.Empty));

            var rank = 0;
            foreach (var hit in hits)
            {
                rank++;
                var modified = hit.SourceModifiedAt?.ToString("yyyy-MM-dd") ?? "unknown";
                var snippet = hit.Content.Length > 240 ? hit.Content[..240] + "…" : hit.Content;

                Ui.Out.Write(new Rule($"[bold]#{rank}[/]  {ScoreBar(hit.Score)} [dim]{hit.Score:F2}[/]")
                    .RuleStyle(new Style(Color.Grey))
                    .LeftJustified());
                Ui.Out.MarkupLine($"[bold {Ui.Accent}]{Ui.E(hit.HeadingPath)}[/]");
                Ui.Out.MarkupLine($"[dim]{Ui.E(Path.GetFileName(hit.SourcePath))} · {Ui.E(hit.Project)} · modified {Ui.E(modified)}[/]");
                Ui.Out.WriteLine(snippet.ReplaceLineEndings(" "));
                Ui.Out.WriteLine();
            }

            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rtfm search: {ex.Message}");
            return 1;
        }
    }

    /// <summary>A 10-cell bar for a hybrid score in [0,1]; BM25 fallback scores clamp at full.</summary>
    private static string ScoreBar(double score)
    {
        var filled = (int)Math.Clamp(Math.Round(score * 10), 0, 10);
        return $"[{Ui.Accent}]{new string('█', filled)}[/][grey]{new string('░', 10 - filled)}[/]";
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
