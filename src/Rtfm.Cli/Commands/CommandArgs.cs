namespace Rtfm.Cli.Commands;

/// <summary>Shared argument parsing for commands that take a folder + optional <c>--project</c>.</summary>
internal static class CommandArgs
{
    /// <summary>
    /// Returns the first positional argument as the folder (null if absent or if
    /// <c>--project</c> is given without a value) and the project name
    /// (default <c>"default"</c>, §2.14).
    /// </summary>
    public static (string? Folder, string Project) ParseFolderAndProject(string[] args)
    {
        string? folder = null;
        var project = "default";

        for (var i = 0; i < args.Length; i++)
        {
            if (args[i] is "--project" or "-p")
            {
                if (i + 1 >= args.Length)
                {
                    return (null, project);
                }

                project = args[++i];
            }
            else
            {
                folder ??= args[i];
            }
        }

        return (folder, project);
    }
}
