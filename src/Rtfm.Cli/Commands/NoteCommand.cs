using Rtfm.Core.Notes;
using Rtfm.Core.OpenSearch;
using Spectre.Console;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm note &lt;add|list|rm&gt;</c> — override notes (§2.13 C, Phase 13):
/// user-confirmed corrections that live in their own index and therefore
/// survive any re-index of the corpus. Typing <c>note add</c> *is* the
/// confirmation — no extra prompt.
/// </summary>
internal static class NoteCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage();
        }

        return args[0] switch
        {
            "add" => await AddAsync(args[1..]).ConfigureAwait(false),
            "list" => await ListAsync(args[1..]).ConfigureAwait(false),
            "rm" or "remove" => await RemoveAsync(args[1..]).ConfigureAwait(false),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            usage: rtfm note add <text> [--project <name>] [--doc <path>] [--author <name>]
                   rtfm note list [--project <name>]
                   rtfm note rm <id>
            """);
        return 2;
    }

    private static async Task<int> AddAsync(string[] args)
    {
        string? text = null, project = null, doc = null, author = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
                case "--doc" when i + 1 < args.Length: doc = args[++i]; break;
                case "--author" when i + 1 < args.Length: author = args[++i]; break;
                default: text = text is null ? args[i] : $"{text} {args[i]}"; break;
            }
        }

        if (string.IsNullOrWhiteSpace(text))
        {
            return Usage();
        }

        using var embedder = await EmbedderProvider.TryCreateAsync().ConfigureAwait(false);
        var store = new NotesStore(new OpenSearchGateway(), embedder);
        var note = await store.AddAsync(text, project ?? "default", doc, author).ConfigureAwait(false);

        Ui.Err.MarkupLine(
            $"[green]Noted[/] [bold]{note.Id}[/] in project [{Ui.Accent}]{Ui.E(note.Project)}[/]"
            + (note.TargetPath is null ? string.Empty : $" [dim]anchored to {Ui.E(Path.GetFileName(note.TargetPath))}[/]")
            + $" [dim]by {Ui.E(note.Author)}[/].");
        return 0;
    }

    private static async Task<int> ListAsync(string[] args)
    {
        string? project = null;
        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--project" or "-p" && i + 1 < args.Length)
            {
                project = args[++i];
            }
        }

        var notes = await new NotesStore(new OpenSearchGateway()).ListAsync(project).ConfigureAwait(false);
        if (notes.Count == 0)
        {
            Ui.Out.MarkupLine("[dim]No override notes.[/]");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .AddColumn("[bold]Id[/]")
            .AddColumn("[bold]Project[/]")
            .AddColumn("[bold]Note[/]")
            .AddColumn("[bold]Anchor[/]")
            .AddColumn("[bold]Author[/]")
            .AddColumn("[bold]Created[/]");

        foreach (var note in notes)
        {
            table.AddRow(
                new Text(note.Id),
                new Markup($"[{Ui.Accent}]{Ui.E(note.Project)}[/]"),
                new Text(note.Text),
                new Text(note.TargetPath is null ? "—" : Path.GetFileName(note.TargetPath)),
                new Text(note.Author),
                new Text(note.CreatedAt.ToLocalTime().ToString("yyyy-MM-dd")));
        }

        Ui.Out.Write(table);
        return 0;
    }

    private static async Task<int> RemoveAsync(string[] args)
    {
        if (args.Length != 1)
        {
            return Usage();
        }

        var removed = await new NotesStore(new OpenSearchGateway()).RemoveAsync(args[0]).ConfigureAwait(false);
        Console.Error.WriteLine(removed ? $"Removed note {args[0]}." : $"No note with id '{args[0]}'.");
        return removed ? 0 : 1;
    }
}
