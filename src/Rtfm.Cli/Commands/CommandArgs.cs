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

    /// <summary>
    /// Parses <c>watch</c>'s arguments: one or more positional folders, an
    /// optional <c>--project</c>/<c>-p</c>, and the <c>--all</c>/<c>-a</c> flag.
    /// <paramref name="args"/> may name several folders (all under one project).
    /// <c>Project</c> is null when unspecified — the caller applies the
    /// <c>"default"</c> project for explicit folders, or "no filter" for
    /// <c>--all</c> (§2.14). <c>Ok</c> is false on a dangling <c>--project</c> or
    /// an unknown flag.
    /// </summary>
    public static (IReadOnlyList<string> Folders, string? Project, bool All, bool Ok) ParseWatchTargets(string[] args)
    {
        var folders = new List<string>();
        string? project = null;
        var all = false;

        for (var i = 0; i < args.Length; i++)
        {
            var arg = args[i];
            if (arg is "--all" or "-a")
            {
                all = true;
            }
            else if (arg is "--project" or "-p")
            {
                if (i + 1 >= args.Length)
                {
                    return (folders, project, all, false);
                }

                project = args[++i];
            }
            else if (arg.StartsWith('-'))
            {
                return (folders, project, all, false); // unknown flag
            }
            else
            {
                folders.Add(arg);
            }
        }

        return (folders, project, all, true);
    }
}
