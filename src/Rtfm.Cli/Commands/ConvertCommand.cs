using Rtfm.Core.Conversion;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm convert &lt;path&gt;</c> — dev aid: convert a single document to
/// markdown and write it to stdout. Used to eyeball conversion fidelity while
/// building the pipeline (Phase 1). Diagnostics go to stderr.
/// </summary>
internal static class ConvertCommand
{
    public static int Run(string[] args)
    {
        if (args.Length == 0)
        {
            Console.Error.WriteLine("usage: rtfm convert <path>");
            return 2;
        }

        var path = args[0];
        if (!File.Exists(path))
        {
            Console.Error.WriteLine($"rtfm convert: file not found: {path}");
            return 1;
        }

        try
        {
            var result = new DocumentConverter().Convert(path);
            Console.Error.WriteLine($"# format={result.Format} title={result.Title ?? "(none)"} chars={result.Markdown.Length}");
            Console.Out.WriteLine(result.Markdown);
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"rtfm convert: {ex.Message}");
            return 1;
        }
    }
}
