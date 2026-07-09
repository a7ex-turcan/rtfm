using Rtfm.Cli;
using Rtfm.Cli.Commands;
using Spectre.Console;

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    return PrintUsage();
}

if (args[0] is "--version" or "-v" or "version")
{
    Ui.Out.MarkupLine($"[bold]rtfm[/] [{Ui.Accent}]{RtfmVersion.Current}[/]");
    return 0;
}

switch (args[0])
{
    case "init":
        return await InitCommand.RunAsync(args[1..]);

    case "ping":
        return await PingCommand.RunAsync(args[1..]);

    case "convert":
        return ConvertCommand.Run(args[1..]);

    case "chunk":
        return ChunkCommand.Run(args[1..]);

    case "index":
        return await IndexCommand.RunAsync(args[1..]);

    case "search":
        return await SearchCommand.RunAsync(args[1..]);

    case "watch":
        return await WatchCommand.RunAsync(args[1..]);

    case "purge":
        return await PurgeCommand.RunAsync(args[1..]);

    case "status":
        return await StatusCommand.RunAsync(args[1..]);

    case "contradictions":
        return await ContradictionsCommand.RunAsync(args[1..]);

    case "note":
        return await NoteCommand.RunAsync(args[1..]);

    default:
        Console.Error.WriteLine($"rtfm: unknown command '{args[0]}'. Run 'rtfm --help'.");
        return 1;
}

static int PrintUsage()
{
    // The logo: an orange prompt chevron, RTFM in the terminal's own foreground,
    // and an orange block cursor. Rendered by hand (three colored segments per
    // row) — a figlet font can't do per-glyph color.
    string[] prompt =
    [
        "██╗   ",
        "╚██╗  ",
        " ╚██╗ ",
        " ██╔╝ ",
        "██╔╝  ",
        "╚═╝   ",
    ];
    string[] word =
    [
        "██████╗ ████████╗███████╗███╗   ███╗",
        "██╔══██╗╚══██╔══╝██╔════╝████╗ ████║",
        "██████╔╝   ██║   █████╗  ██╔████╔██║",
        "██╔══██╗   ██║   ██╔══╝  ██║╚██╔╝██║",
        "██║  ██║   ██║   ██║     ██║ ╚═╝ ██║",
        "╚═╝  ╚═╝   ╚═╝   ╚═╝     ╚═╝     ╚═╝",
    ];
    string[] cursor =
    [
        "████",
        "████",
        "████",
        "████",
        "████",
        "████",
    ];

    Ui.Out.WriteLine();
    for (var i = 0; i < prompt.Length; i++)
    {
        Ui.Out.MarkupLine($"[{Ui.Accent}]{prompt[i]}[/] {word[i]}  [{Ui.Accent}]{cursor[i]}[/]");
    }

    Ui.Out.WriteLine();
    Ui.Out.MarkupLine($"[bold]R[/]etrieval [bold]T[/]ool [bold]F[/]or [bold]M[/]anuals [dim]v{RtfmVersion.Current}[/] — [dim]the answer was in the docs all along.[/]");
    Ui.Out.WriteLine();

    var commands = new Table()
        .Border(TableBorder.Rounded)
        .BorderColor(Color.Grey)
        .Title("[bold]Commands[/]")
        .AddColumn(new TableColumn("[bold]Command[/]").NoWrap())
        .AddColumn("[bold]Description[/]");

    commands.AddRow($"[{Ui.Accent}]init[/] [dim][[--with-model]][/]", "Bootstrap: start OpenSearch (docker), create index + pipeline");
    commands.AddRow($"[{Ui.Accent}]ping[/]", "Check connectivity to OpenSearch");
    commands.AddRow($"[{Ui.Accent}]index[/] [dim]<folder> [[--project <name>]][/]", "(Re)index a folder (default project \"default\")");
    commands.AddRow($"[{Ui.Accent}]watch[/] [dim]<folder...> [[--project <name>]] | --all[/]", "Watch one/many folders (or --all indexed) and keep the index fresh");
    commands.AddRow($"[{Ui.Accent}]search[/] [dim]<query...> [[--project <name>|--all]][/]", "Hybrid search (lexical + semantic; omit --project to span all)");
    commands.AddRow($"[{Ui.Accent}]status[/] [dim][[--project <name>]] [[--stale <days>]][/]", "Index health: projects, counts, vector coverage, staleness");
    commands.AddRow($"[{Ui.Accent}]contradictions[/] [dim][[--project]] [[--closed]] | dismiss <id> | resolve <id> --note <text>[/]", "Doc-vs-doc disagreements: list, dismiss, or resolve into an override note");
    commands.AddRow($"[{Ui.Accent}]note[/] [dim]add <text>|list|rm <id> [[--project]] [[--doc <path>]][/]", "Override notes: corrections that survive re-indexing");
    commands.AddRow($"[{Ui.Accent}]purge[/] [dim]<project> [[--yes]][/]", "Remove a project's chunks and watch manifests (asks first)");
    commands.AddRow($"[{Ui.Accent}]convert[/] [dim]<path>[/]", "Convert one document to markdown (stdout)");
    commands.AddRow($"[{Ui.Accent}]chunk[/] [dim]<path>[/]", "Convert then show heading-aware chunks (stdout)");
    commands.AddRow($"[{Ui.Accent}]--version[/] [dim](-v)[/]", "Print the rtfm version");
    Ui.Out.Write(commands);

    var env = new Table()
        .Border(TableBorder.Rounded)
        .BorderColor(Color.Grey)
        .Title("[bold]Environment[/]")
        .AddColumn(new TableColumn("[bold]Variable[/]").NoWrap())
        .AddColumn("[bold]Meaning[/]");

    env.AddRow("[teal]RTFM_OPENSEARCH_URL[/]", "OpenSearch endpoint [dim](default http://localhost:9200)[/]");
    env.AddRow("[teal]RTFM_PROJECT[/]", "Default project scope for the MCP server");
    env.AddRow("[teal]RTFM_MODEL_DIR[/]", "Embedding model cache override [dim](default LocalApplicationData/rtfm/models)[/]");
    Ui.Out.Write(env);

    return 0;
}
