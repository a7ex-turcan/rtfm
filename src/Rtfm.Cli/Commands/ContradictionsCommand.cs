using Rtfm.Core.Contradictions;
using Rtfm.Core.OpenSearch;
using Spectre.Console;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm contradictions [--project &lt;name&gt;]</c> — lists nominated
/// contradiction pairs, newest detection first (§2.13, Phase 12). These are
/// *nominations*, not verdicts: semantically-similar chunks from different
/// documents in the same project whose content may disagree. Reading both and
/// deciding is a human/LLM job.
/// </summary>
internal static class ContradictionsCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
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
                    Console.Error.WriteLine("usage: rtfm contradictions [--project <name>]");
                    return 2;
            }
        }

        var detector = new ContradictionDetector(new OpenSearchGateway());
        var pairs = await Ui.Err.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Loading nominations…", _ => detector.ListAsync(project));

        var scope = string.IsNullOrEmpty(project) ? "all projects" : $"project '{project}'";
        if (pairs.Count == 0)
        {
            Ui.Out.MarkupLine($"[green]No contradiction nominations[/] [dim]({Ui.E(scope)})[/].");
            return 0;
        }

        Ui.Err.MarkupLine($"[bold]{pairs.Count}[/] nomination{(pairs.Count == 1 ? "" : "s")} [dim]({Ui.E(scope)}) — newer side first; verify before trusting either.[/]");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn(new TableColumn("[bold]Sim[/]").RightAligned())
            .AddColumn("[bold]Project[/]")
            .AddColumn("[bold]Newer[/]")
            .AddColumn("[bold]Older[/]");

        foreach (var pair in pairs)
        {
            table.AddRow(
                new Text(pair.Similarity.ToString("F2")),
                new Markup($"[{Ui.Accent}]{Ui.E(pair.Project)}[/]"),
                Side(pair.A),
                Side(pair.B));
        }

        Ui.Out.Write(table);
        return 0;
    }

    private static Markup Side(ContradictionSide side)
    {
        var date = side.ModifiedAt?.ToString("yyyy-MM-dd") ?? "unknown";
        var excerpt = side.Excerpt.Length > 120 ? side.Excerpt[..120] + "…" : side.Excerpt;
        return new Markup(
            $"[bold]{Ui.E(Path.GetFileName(side.Path))}[/] [dim]· {Ui.E(date)}[/]\n"
            + $"[dim]{Ui.E(side.Heading)}[/]\n"
            + $"[italic]\"{Ui.E(excerpt)}\"[/]");
    }
}
