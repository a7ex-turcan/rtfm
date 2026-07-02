using Rtfm.Cli.Commands;

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    return PrintUsage();
}

switch (args[0])
{
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

    default:
        Console.Error.WriteLine($"rtfm: unknown command '{args[0]}'. Run 'rtfm --help'.");
        return 1;
}

static int PrintUsage()
{
    Console.WriteLine(
        """
        rtfm — Retrieval Tool For Manuals

        Usage:
          rtfm ping             Check connectivity to OpenSearch
          rtfm convert <path>   Convert one document to markdown (stdout)
          rtfm chunk <path>     Convert then show heading-aware chunks (stdout)
          rtfm index <folder> [--project <name>]   (Re)index a folder (default project "default")
          rtfm watch <folder> [--project <name>]   Watch a folder and keep the index fresh (Ctrl+C to stop)
          rtfm search <query> [--project <name>]    Tier 1 search (omit --project to span all)
          rtfm --help           Show this help

        Environment:
          RTFM_OPENSEARCH_URL   OpenSearch endpoint (default http://localhost:9200)
        """);
    return 0;
}
