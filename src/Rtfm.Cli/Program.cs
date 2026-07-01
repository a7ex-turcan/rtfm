using Rtfm.Cli.Commands;

if (args.Length == 0 || args[0] is "--help" or "-h" or "help")
{
    return PrintUsage();
}

switch (args[0])
{
    case "ping":
        return await PingCommand.RunAsync(args[1..]);

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
          rtfm ping        Check connectivity to OpenSearch
          rtfm --help      Show this help

        Environment:
          RTFM_OPENSEARCH_URL   OpenSearch endpoint (default http://localhost:9200)
        """);
    return 0;
}
