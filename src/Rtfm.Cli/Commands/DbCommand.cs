using Rtfm.Core.Configuration;
using Rtfm.Core.Database;
using Spectre.Console;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm db [list|query]</c> — the Phase 23 live-data gateway from the console.
/// <c>list</c> enumerates queryable <c>.rtfmdb</c> connectors; <c>query</c> runs a
/// read-only SQL statement and prints the rows. Unlike the rest of the CLI this
/// talks straight to the database (§2.15); the read-only boundary lives in
/// <see cref="DatabaseQueryService"/>, not here.
/// </summary>
internal static class DbCommand
{
    public static async Task<int> RunAsync(string[] args)
    {
        return args.FirstOrDefault() switch
        {
            "list" or null => List(args.Length == 0 ? args : args[1..]),
            "query" => await QueryAsync(args[1..]).ConfigureAwait(false),
            _ => Usage(),
        };
    }

    private static int Usage()
    {
        Console.Error.WriteLine(
            """
            usage: rtfm db list [--project <name>]
                   rtfm db query <name> "<sql>" [--project <name>] [--max-rows <n>]
            """);
        return 2;
    }

    private static int List(string[] args)
    {
        string? project = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
                case "--all": project = "*"; break;
                default: return Usage();
            }
        }

        var scope = RtfmEnvironment.ResolveProjectScope(project);
        var databases = DatabaseRegistry.List(scope);
        var scopeLabel = scope ?? "all projects";

        if (databases.Count == 0)
        {
            Ui.Out.MarkupLine($"[yellow]No .rtfmdb connectors found[/] [dim]({Ui.E(scopeLabel)})[/]. Add one and run 'rtfm index'.");
            return 0;
        }

        var table = new Table()
            .Border(TableBorder.Rounded)
            .BorderColor(Color.Grey)
            .Title($"[bold]Databases[/] [dim]({Ui.E(scopeLabel)})[/]")
            .AddColumn("[bold]Handle[/]")
            .AddColumn("[bold]Name[/]")
            .AddColumn("[bold]Project[/]")
            .AddColumn("[bold]Provider[/]")
            .AddColumn("[bold]Access[/]");

        foreach (var db in databases)
        {
            table.AddRow(
                $"[{Ui.Accent}]{Ui.E(db.Handle)}[/]",
                Ui.E(db.Name),
                Ui.E(db.Project),
                Ui.E(db.Provider),
                (db.Queryable, db.Writable) switch
                {
                    (false, _) => "[dim]— schema only[/]",
                    (true, true) => "[yellow]read+write[/]",
                    _ => "[green]read-only[/]",
                });
        }

        Ui.Out.Write(table);
        return 0;
    }

    private static async Task<int> QueryAsync(string[] args)
    {
        string? name = null, sql = null, project = null;
        int? maxRows = null;
        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--project" or "-p" when i + 1 < args.Length: project = args[++i]; break;
                case "--max-rows" or "-n" when i + 1 < args.Length && int.TryParse(args[i + 1], out var n): maxRows = n; i++; break;
                default:
                    if (name is null) { name = args[i]; }
                    else if (sql is null) { sql = args[i]; }
                    else { return Usage(); }

                    break;
            }
        }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(sql))
        {
            return Usage();
        }

        var scope = RtfmEnvironment.ResolveProjectScope(project);
        var info = DatabaseRegistry.Resolve(name, scope);
        if (info is null)
        {
            Ui.Err.MarkupLine($"[red]No database[/] [bold]{Ui.E(name)}[/] [dim](scope: {Ui.E(scope ?? "all projects")})[/]. Try [italic]rtfm db list[/].");
            return 1;
        }

        var service = new DatabaseQueryService();
        var result = await Ui.Err.Status()
            .Spinner(Spinner.Known.Dots)
            .StartAsync($"Querying {info.Handle}…", _ => service.ExecuteAsync(info, sql, maxRows));

        if (!result.Success)
        {
            Ui.Err.MarkupLine($"[red]Query refused/failed:[/] {Ui.E(result.Error ?? "unknown error")}");
            return 1;
        }

        if (result.Columns.Count == 0)
        {
            Ui.Err.MarkupLine(result.RowsAffected > 0
                ? $"[green]OK[/] [dim]— {result.RowsAffected} row(s) affected.[/]"
                : "[green]OK[/] [dim]— no rows returned.[/]");
            return 0;
        }

        // Results go to stdout (the stream contract): a plain-parseable table when
        // redirected, a pretty one on a live terminal.
        var table = new Table().Border(TableBorder.Rounded).BorderColor(Color.Grey);
        foreach (var column in result.Columns)
        {
            table.AddColumn($"[bold]{Ui.E(column)}[/]");
        }

        foreach (var row in result.Rows)
        {
            table.AddRow(row.Select(c => new Markup(c is null ? "[dim]NULL[/]" : Ui.E(c))).ToArray());
        }

        Ui.Out.Write(table);

        Ui.Err.MarkupLine(result.Truncated
            ? $"[yellow]{result.RowCount} rows (truncated — more exist; narrow the query).[/]"
            : $"[dim]{result.RowCount} row{(result.RowCount == 1 ? "" : "s")}.[/]");
        return 0;
    }
}
