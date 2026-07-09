using System.Text;
using System.Text.Json.Nodes;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm mcp-config [--client &lt;name&gt;] [--project &lt;name&gt;] [--dll &lt;path&gt;]</c>
/// — prints a ready-to-paste MCP server config snippet for a given client.
///
/// The RTFM MCP server is a standard stdio server, so any MCP-capable client can
/// use it — only each client's *config file format* differs. This command emits
/// the right shape (most share <c>mcpServers</c>; VS Code uses <c>servers</c> +
/// <c>type: "stdio"</c>, Zed uses <c>context_servers</c>, Continue uses YAML).
///
/// Stream contract: the snippet is the *result* → stdout (raw, pipeable; never
/// through Spectre, whose markup would eat the JSON brackets). Guidance —
/// which file, caveats — goes to stderr.
/// </summary>
internal static class McpConfigCommand
{
    private sealed record ClientInfo(string Key, string DisplayName, string Flavor, string FileHint, string? VerifyNote = null);

    private static readonly ClientInfo[] Clients =
    [
        new("claude-code", "Claude Code", "mcpServers",
            ".mcp.json at the repo root (project-scoped, safe to commit)."),
        new("claude-desktop", "Claude Desktop", "mcpServers",
            "claude_desktop_config.json (Settings -> Developer -> Edit Config). GUI-launched, so it may not see your PATH — prefer the absolute path to the rtfm-mcp shim (e.g. ~/.dotnet/tools/rtfm-mcp)."),
        new("cursor", "Cursor", "mcpServers",
            ".cursor/mcp.json in this project, or ~/.cursor/mcp.json for all projects."),
        new("windsurf", "Windsurf", "mcpServers",
            "~/.codeium/windsurf/mcp_config.json (or Cascade -> MCP -> Manage -> View raw config)."),
        new("cline", "Cline", "mcpServers",
            "Cline extension -> MCP Servers -> Configure (cline_mcp_settings.json)."),
        new("vscode", "VS Code (Copilot agent mode)", "vscode",
            ".vscode/mcp.json in the workspace. Requires agent mode; note the key is 'servers', not 'mcpServers'."),
        new("zed", "Zed", "zed",
            "Zed settings.json, under 'context_servers'."),
        new("continue", "Continue", "continue",
            "~/.continue/config.yaml.",
            "Continue's MCP config schema has changed across versions — verify this against Continue's current docs."),
    ];

    public static int Run(string[] args)
    {
        string? clientArg = null;
        string? project = null;
        string? dll = null;

        for (var i = 0; i < args.Length; i++)
        {
            switch (args[i])
            {
                case "--client" or "-c" when i + 1 < args.Length:
                    clientArg = args[++i];
                    break;
                case "--project" or "-p" when i + 1 < args.Length:
                    project = args[++i];
                    break;
                case "--dll" when i + 1 < args.Length:
                    dll = args[++i];
                    break;
                default:
                    Console.Error.WriteLine($"rtfm mcp-config: unexpected argument '{args[i]}'.");
                    PrintClientList();
                    return 2;
            }
        }

        if (clientArg is null)
        {
            PrintClientList();
            return 0;
        }

        var client = Resolve(clientArg);
        if (client is null)
        {
            Console.Error.WriteLine($"rtfm mcp-config: unknown client '{clientArg}'.");
            PrintClientList();
            return 2;
        }

        var resolvedProject = project
            ?? Environment.GetEnvironmentVariable("RTFM_PROJECT")
            ?? "default";
        var openSearchUrl = Environment.GetEnvironmentVariable("RTFM_OPENSEARCH_URL")
            ?? "http://localhost:9200";

        // Guidance → stderr.
        Console.Error.WriteLine($"# {client.DisplayName} — put this in: {client.FileHint}");
        if (dll is not null)
        {
            Console.Error.WriteLine("# Running from a clone (built DLL). Build in Release first; a running server locks the DLL.");
        }
        else
        {
            Console.Error.WriteLine("# Assumes the 'rtfm-mcp' global tool is on PATH (dotnet tool install -g Rtfm.Mcp).");
        }

        if (client.VerifyNote is not null)
        {
            Console.Error.WriteLine($"# NOTE: {client.VerifyNote}");
        }

        // Snippet → stdout (raw).
        Console.Out.WriteLine(Render(client.Flavor, resolvedProject, openSearchUrl, dll));
        return 0;
    }

    private static ClientInfo? Resolve(string key)
    {
        var normalized = key.Trim().ToLowerInvariant() switch
        {
            "claude" or "claudecode" => "claude-code",
            "desktop" or "claude_desktop" or "claudedesktop" => "claude-desktop",
            "code" or "vs-code" or "vscode-insiders" => "vscode",
            "copilot" => "vscode",
            _ => key.Trim().ToLowerInvariant(),
        };

        return Clients.FirstOrDefault(c => c.Key == normalized);
    }

    private static string Render(string flavor, string project, string openSearchUrl, string? dll)
    {
        if (flavor == "continue")
        {
            return RenderContinueYaml(project, openSearchUrl, dll);
        }

        var env = new JsonObject
        {
            ["RTFM_OPENSEARCH_URL"] = openSearchUrl,
            ["RTFM_PROJECT"] = project,
        };

        var server = new JsonObject();
        if (flavor == "vscode")
        {
            server["type"] = "stdio";
        }
        else if (flavor == "zed")
        {
            server["source"] = "custom";
        }

        if (dll is null)
        {
            server["command"] = "rtfm-mcp";
            // Zed's schema wants "args" present even when empty.
            if (flavor == "zed")
            {
                server["args"] = new JsonArray();
            }
        }
        else
        {
            server["command"] = "dotnet";
            server["args"] = new JsonArray(dll);
        }

        server["env"] = env;

        var rtfm = new JsonObject { ["rtfm"] = server };
        var root = flavor switch
        {
            "vscode" => new JsonObject { ["servers"] = rtfm },
            "zed" => new JsonObject { ["context_servers"] = rtfm },
            _ => new JsonObject { ["mcpServers"] = rtfm },
        };

        return root.ToJsonString(new System.Text.Json.JsonSerializerOptions { WriteIndented = true });
    }

    private static string RenderContinueYaml(string project, string openSearchUrl, string? dll)
    {
        var sb = new StringBuilder();
        sb.AppendLine("mcpServers:");
        sb.AppendLine("  - name: rtfm");
        if (dll is null)
        {
            sb.AppendLine("    command: rtfm-mcp");
        }
        else
        {
            sb.AppendLine("    command: dotnet");
            sb.AppendLine("    args:");
            sb.AppendLine($"      - {dll}");
        }

        sb.AppendLine("    env:");
        sb.AppendLine($"      RTFM_OPENSEARCH_URL: {openSearchUrl}");
        sb.AppendLine($"      RTFM_PROJECT: {project}");
        return sb.ToString().TrimEnd();
    }

    private static void PrintClientList()
    {
        Console.Error.WriteLine("usage: rtfm mcp-config --client <name> [--project <name>] [--dll <path>]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Supported clients:");
        foreach (var c in Clients)
        {
            Console.Error.WriteLine($"  {c.Key,-16} {c.DisplayName}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("The snippet prints to stdout (pipeable); notes print to stderr.");
        Console.Error.WriteLine("Default uses the installed 'rtfm-mcp' tool; pass --dll <path> for a from-source build.");
    }
}
