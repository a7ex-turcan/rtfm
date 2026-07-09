using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Rtfm.Cli.Commands;

/// <summary>
/// <c>rtfm mcp-config [--client &lt;name&gt;] [--project &lt;name&gt;] [--dll &lt;path&gt;]
/// [--write [--file &lt;path&gt;]]</c> — prints (or merges) a ready-to-paste MCP
/// server config snippet for a given client.
///
/// The RTFM MCP server is a standard stdio server, so any MCP-capable client can
/// use it — only each client's *config file format* differs. This command emits
/// the right shape (most share <c>mcpServers</c>; VS Code uses <c>servers</c> +
/// <c>type: "stdio"</c>, Zed uses <c>context_servers</c>, Continue uses YAML).
///
/// With <c>--write</c> it merges the <c>rtfm</c> server into an existing JSON
/// config in place (idempotent, backup first), rather than only printing it.
///
/// Stream contract: a printed snippet is the *result* → stdout (raw, pipeable;
/// never through Spectre, whose markup would eat the JSON brackets). Guidance,
/// and write-mode status, go to stderr.
/// </summary>
internal static class McpConfigCommand
{
    private sealed record ClientInfo(
        string Key, string DisplayName, string Flavor, string FileHint,
        string? VerifyNote = null, string? DefaultWriteTarget = null);

    private static readonly ClientInfo[] Clients =
    [
        new("claude-code", "Claude Code", "mcpServers",
            ".mcp.json at the repo root (project-scoped, safe to commit).",
            DefaultWriteTarget: ".mcp.json"),
        new("claude-desktop", "Claude Desktop", "mcpServers",
            "claude_desktop_config.json (Settings -> Developer -> Edit Config). GUI-launched, so it may not see your PATH — prefer the absolute path to the rtfm-mcp shim (e.g. ~/.dotnet/tools/rtfm-mcp)."),
        new("cursor", "Cursor", "mcpServers",
            ".cursor/mcp.json in this project, or ~/.cursor/mcp.json for all projects.",
            DefaultWriteTarget: ".cursor/mcp.json"),
        new("windsurf", "Windsurf", "mcpServers",
            "~/.codeium/windsurf/mcp_config.json (or Cascade -> MCP -> Manage -> View raw config)."),
        new("cline", "Cline", "mcpServers",
            "Cline extension -> MCP Servers -> Configure (cline_mcp_settings.json)."),
        new("vscode", "VS Code (Copilot agent mode)", "vscode",
            ".vscode/mcp.json in the workspace. Requires agent mode; note the key is 'servers', not 'mcpServers'.",
            DefaultWriteTarget: ".vscode/mcp.json"),
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
        string? file = null;
        var write = false;
        var force = false;

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
                case "--file" or "-f" when i + 1 < args.Length:
                    file = args[++i];
                    break;
                case "--write" or "-w":
                    write = true;
                    break;
                case "--force" or "-F":
                    force = true;
                    break;
                default:
                    Console.Error.WriteLine($"rtfm mcp-config: unexpected argument '{args[i]}'.");
                    PrintClientList();
                    return 2;
            }
        }

        if (file is not null && !write)
        {
            Console.Error.WriteLine("rtfm mcp-config: --file only applies with --write.");
            return 2;
        }

        if (force && !write)
        {
            Console.Error.WriteLine("rtfm mcp-config: --force only applies with --write.");
            return 2;
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

        return write
            ? WriteConfig(client, resolvedProject, openSearchUrl, dll, file, force)
            : PrintConfig(client, resolvedProject, openSearchUrl, dll);
    }

    private static int PrintConfig(ClientInfo client, string project, string url, string? dll)
    {
        Console.Error.WriteLine($"# {client.DisplayName} — put this in: {client.FileHint}");
        Console.Error.WriteLine(dll is not null
            ? "# Running from a clone (built DLL). Build in Release first; a running server locks the DLL."
            : "# Assumes the 'rtfm-mcp' global tool is on PATH (dotnet tool install -g Rtfm.Mcp).");
        if (client.VerifyNote is not null)
        {
            Console.Error.WriteLine($"# NOTE: {client.VerifyNote}");
        }

        Console.Out.WriteLine(Render(client.Flavor, project, url, dll));
        return 0;
    }

    /// <summary>Merges the <c>rtfm</c> server into a JSON config file in place, idempotently, backing up first.</summary>
    private static int WriteConfig(ClientInfo client, string project, string url, string? dll, string? fileArg, bool force)
    {
        if (client.Flavor == "continue")
        {
            // YAML — editing it in place would need a YAML round-tripper we don't ship.
            Console.Error.WriteLine("rtfm mcp-config: --write can't safely edit Continue's YAML config. Paste this into your config.yaml:");
            Console.Out.WriteLine(RenderContinueYaml(project, url, dll));
            return 1;
        }

        var target = fileArg ?? client.DefaultWriteTarget;
        if (target is null)
        {
            Console.Error.WriteLine($"rtfm mcp-config: for {client.DisplayName}, pass --file <path> to the config file to merge into.");
            return 2;
        }

        var existed = File.Exists(target);
        JsonObject root;

        if (existed)
        {
            string text;
            try
            {
                text = File.ReadAllText(target);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                Console.Error.WriteLine($"rtfm mcp-config: can't read {target}: {ex.Message}");
                return 1;
            }

            if (text.Trim().Length == 0)
            {
                root = new JsonObject();
            }
            else
            {
                // Refuse to rewrite a commented (JSONC) file by default —
                // re-serializing can't preserve comments. --force overrides
                // (comments are dropped; the .bak is the safety net).
                var commented = HasComments(text);
                if (commented && !force)
                {
                    Console.Error.WriteLine($"rtfm mcp-config: {target} contains comments (JSONC); not rewriting it (that would lose them).");
                    Console.Error.WriteLine("Paste this block into it manually, or re-run with --force to rewrite anyway (comments dropped; a .bak is kept):");
                    Console.Out.WriteLine(Render(client.Flavor, project, url, dll));
                    return 1;
                }

                JsonNode? parsed;
                try
                {
                    // Lenient parse so --force can read JSONC; comments are then
                    // dropped on re-serialize (the whole reason for the refusal above).
                    parsed = JsonNode.Parse(text, documentOptions: new JsonDocumentOptions
                    {
                        CommentHandling = JsonCommentHandling.Skip,
                        AllowTrailingCommas = true,
                    });
                }
                catch (JsonException ex)
                {
                    Console.Error.WriteLine($"rtfm mcp-config: {target} isn't valid JSON: {ex.Message}");
                    return 1;
                }

                if (commented)
                {
                    Console.Error.WriteLine($"rtfm mcp-config: --force: rewriting {target} without its comments (backup kept as {target}.bak).");
                }

                if (parsed is not JsonObject obj)
                {
                    Console.Error.WriteLine($"rtfm mcp-config: {target} isn't a JSON object at the top level; not editing it.");
                    return 1;
                }

                root = obj;
            }
        }
        else
        {
            root = new JsonObject();
        }

        var containerKey = ContainerKey(client.Flavor);
        if (root[containerKey] is not JsonObject container)
        {
            container = new JsonObject();
            root[containerKey] = container;
        }

        var replaced = container["rtfm"] is not null;
        container["rtfm"] = BuildServerNode(client.Flavor, project, url, dll);

        var json = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });

        try
        {
            if (existed)
            {
                File.Copy(target, target + ".bak", overwrite: true);
            }
            else
            {
                var dir = Path.GetDirectoryName(Path.GetFullPath(target));
                if (!string.IsNullOrEmpty(dir))
                {
                    Directory.CreateDirectory(dir);
                }
            }

            File.WriteAllText(target, json + Environment.NewLine);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine($"rtfm mcp-config: can't write {target}: {ex.Message}");
            return 1;
        }

        var verb = !existed ? "created" : replaced ? "updated (replaced existing 'rtfm')" : "updated (added 'rtfm')";
        Console.Error.WriteLine($"rtfm mcp-config: {verb} {target}{(existed ? $"  (backup: {target}.bak)" : "")}");
        return 0;
    }

    private static ClientInfo? Resolve(string key)
    {
        var normalized = key.Trim().ToLowerInvariant() switch
        {
            "claude" or "claudecode" => "claude-code",
            "desktop" or "claude_desktop" or "claudedesktop" => "claude-desktop",
            "code" or "vs-code" or "vscode-insiders" or "copilot" => "vscode",
            _ => key.Trim().ToLowerInvariant(),
        };

        return Clients.FirstOrDefault(c => c.Key == normalized);
    }

    private static string ContainerKey(string flavor) => flavor switch
    {
        "vscode" => "servers",
        "zed" => "context_servers",
        _ => "mcpServers",
    };

    private static JsonObject BuildServerNode(string flavor, string project, string url, string? dll)
    {
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

        server["env"] = new JsonObject
        {
            ["RTFM_OPENSEARCH_URL"] = url,
            ["RTFM_PROJECT"] = project,
        };
        return server;
    }

    private static string Render(string flavor, string project, string url, string? dll)
    {
        if (flavor == "continue")
        {
            return RenderContinueYaml(project, url, dll);
        }

        var root = new JsonObject
        {
            [ContainerKey(flavor)] = new JsonObject { ["rtfm"] = BuildServerNode(flavor, project, url, dll) },
        };
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    private static string RenderContinueYaml(string project, string url, string? dll)
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
        sb.AppendLine($"      RTFM_OPENSEARCH_URL: {url}");
        sb.AppendLine($"      RTFM_PROJECT: {project}");
        return sb.ToString().TrimEnd();
    }

    /// <summary>True if the JSON text contains a <c>//</c> or <c>/*</c> comment outside a string.</summary>
    private static bool HasComments(string text)
    {
        var inString = false;
        for (var i = 0; i < text.Length; i++)
        {
            var c = text[i];
            if (inString)
            {
                if (c == '\\')
                {
                    i++; // skip the escaped char
                }
                else if (c == '"')
                {
                    inString = false;
                }
            }
            else if (c == '"')
            {
                inString = true;
            }
            else if (c == '/' && i + 1 < text.Length && (text[i + 1] == '/' || text[i + 1] == '*'))
            {
                return true;
            }
        }

        return false;
    }

    private static void PrintClientList()
    {
        Console.Error.WriteLine("usage: rtfm mcp-config --client <name> [--project <name>] [--dll <path>] [--write [--file <path>] [--force]]");
        Console.Error.WriteLine();
        Console.Error.WriteLine("Supported clients:");
        foreach (var c in Clients)
        {
            Console.Error.WriteLine($"  {c.Key,-16} {c.DisplayName}");
        }

        Console.Error.WriteLine();
        Console.Error.WriteLine("Without --write, prints the snippet to stdout (pipeable); notes go to stderr.");
        Console.Error.WriteLine("With --write, merges the 'rtfm' server into a JSON config in place (backup first;");
        Console.Error.WriteLine("refuses to rewrite a file with comments unless --force, which drops them).");
        Console.Error.WriteLine("Default uses the installed 'rtfm-mcp' tool; pass --dll <path> for a from-source build.");
    }
}
