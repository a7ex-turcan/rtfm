using Rtfm.Core.Contradictions;
using Rtfm.Core.Notes;
using Rtfm.Core.OpenSearch;
using Spectre.Console;
using Spectre.Console.Rendering;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm contradictions [list|dismiss|resolve]</c> — nominated contradiction
/// pairs (§2.13, Phase 12) and their Phase 22 lifecycle. Listing shows open
/// nominations newest-first (<c>--closed</c> adds history); <c>dismiss</c>
/// closes a false positive, <c>resolve</c> records the confirmed answer as an
/// override note (typing the command *is* the confirmation, like
/// <c>rtfm note add</c>). Closed pairs survive re-indexing.
/// </summary>
internal static class ContradictionsCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        return args.FirstOrDefault() switch
        {
            "dismiss" => await DismissAsync(args[1..]).ConfigureAwait(false),
            "resolve" => await ResolveAsync(args[1..]).ConfigureAwait(false),
            _ => await ListAsync(args).ConfigureAwait(false),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            usage: rtfm contradictions [--project <name>] [--closed]
                   rtfm contradictions dismiss <id>
                   rtfm contradictions resolve <id> --note <correction text> [--author <name>]
            """);
        return 2;
    }

    private static async Task<int> ListAsync(string[] args)
    {
        string? project = null;
        var includeClosed = false;
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
                case "--closed":
                    includeClosed = true;
                    break;
                default:
                    return Usage();
            }
        }

        var detector = new ContradictionDetector(new OpenSearchGateway());
        var pairs = await Ui.Err.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync("Loading nominations…", _ => detector.ListAsync(project, includeClosed: includeClosed));

        var scope = string.IsNullOrEmpty(project) ? "all projects" : $"project '{project}'";
        if (pairs.Count == 0)
        {
            Ui.Out.MarkupLine($"[green]No {(includeClosed ? "" : "open ")}contradiction nominations[/] [dim]({Ui.E(scope)})[/].");
            return 0;
        }

        Ui.Err.MarkupLine($"[bold]{pairs.Count}[/] nomination{(pairs.Count == 1 ? "" : "s")} [dim]({Ui.E(scope)}) — newer side first; verify before trusting either.[/]");

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Id[/]")
            .AddColumn("[bold]Kind[/]")
            .AddColumn(new TableColumn("[bold]Sim[/]").RightAligned())
            .AddColumn("[bold]Project[/]")
            .AddColumn("[bold]Newer[/]")
            .AddColumn("[bold]Older[/]");

        if (includeClosed)
        {
            table.AddColumn("[bold]Status[/]");
        }

        foreach (var pair in pairs)
        {
            var cells = new List<IRenderable>
            {
                new Text(pair.Id),
                new Markup(pair.Kind == ContradictionPair.KindSupersession
                    ? "[yellow]supersession?[/]"
                    : "[red]contradiction[/]"),
                new Text(pair.Similarity.ToString("F2")),
                new Markup($"[{Ui.Accent}]{Ui.E(pair.Project)}[/]"),
                Side(pair.A),
                Side(pair.B),
            };

            if (includeClosed)
            {
                cells.Add(new Markup(pair.Status switch
                {
                    ContradictionPair.StatusDismissed => "[dim]dismissed[/]",
                    ContradictionPair.StatusResolved => $"[green]resolved[/][dim] → note {Ui.E(pair.ResolvedNoteId ?? "?")}[/]",
                    _ => "open",
                }));
            }

            table.AddRow(cells);
        }

        Ui.Out.Write(table);
        return 0;
    }

    private static async Task<int> DismissAsync(string[] args)
    {
        var id = args.FirstOrDefault(a => !a.StartsWith('-'));
        if (string.IsNullOrWhiteSpace(id))
        {
            return Usage();
        }

        var detector = new ContradictionDetector(new OpenSearchGateway());
        if (await detector.GetAsync(id).ConfigureAwait(false) is null)
        {
            Ui.Err.MarkupLine($"[red]No contradiction pair with id[/] [bold]{Ui.E(id)}[/].");
            return 1;
        }

        await detector.SetStatusAsync(id, ContradictionPair.StatusDismissed).ConfigureAwait(false);
        Ui.Err.MarkupLine($"[green]Dismissed[/] [bold]{Ui.E(id)}[/] [dim](stays dismissed across re-indexing)[/].");
        return 0;
    }

    private static async Task<int> ResolveAsync(string[] args)
    {
        string? id = null, noteText = null, author = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--note" or "-n" when i + 1 < args.Length: noteText = args[++i]; break;
                case "--author" when i + 1 < args.Length: author = args[++i]; break;
                default: id ??= args[i]; break;
            }
        }

        if (string.IsNullOrWhiteSpace(id) || string.IsNullOrWhiteSpace(noteText))
        {
            return Usage();
        }

        var gateway = new OpenSearchGateway();
        var detector = new ContradictionDetector(gateway);
        var pair = await detector.GetAsync(id).ConfigureAwait(false);
        if (pair is null)
        {
            Ui.Err.MarkupLine($"[red]No contradiction pair with id[/] [bold]{Ui.E(id)}[/].");
            return 1;
        }

        using var embedder = await EmbedderProvider.TryCreateAsync().ConfigureAwait(false);
        var note = await new NotesStore(gateway, embedder)
            .AddAsync(noteText, pair.Project, pair.B.Path, author)
            .ConfigureAwait(false);
        await detector.SetStatusAsync(id, ContradictionPair.StatusResolved, note.Id).ConfigureAwait(false);

        Ui.Err.MarkupLine(
            $"[green]Resolved[/] [bold]{Ui.E(id)}[/] — correction saved as note [bold]{note.Id}[/]"
            + $" [dim]anchored to {Ui.E(Path.GetFileName(pair.B.Path))}[/].");
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
