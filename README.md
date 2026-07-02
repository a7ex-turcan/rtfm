# RTFM

**R**etrieval **T**ool **F**or **M**anuals — a local, per-developer documentation
search tool for your LLM.

RTFM indexes a folder of Confluence-exported `.docx` documentation into a local
[OpenSearch](https://opensearch.org/) instance and exposes it to any MCP-capable
LLM client (Claude Code, Claude Desktop, IDE integrations) over a stdio
[Model Context Protocol](https://modelcontextprotocol.io/) server. Instead of
manually attaching docs to a chat, you ask your LLM a question and it retrieves
the relevant passages itself.

It's built to answer two kinds of question:

- **Technical lookup** — *"What's the endpoint to GET this resource?"*
  The answer's words are in the docs verbatim (lexical search).
- **Conceptual** — *"What does Bundle mean?"*
  The answer may never repeat your phrasing (semantic search).

Everything runs locally. No external APIs, no per-developer cloud accounts, no
documents leaving your machine.

## How it works

```
docs/ (.docx)  ──►  rtfm (CLI)  ──►  OpenSearch  ──►  rtfm-mcp  ──►  your LLM
                convert · chunk        rtfm-docs        search_docs      client
                · index                  index            (MCP)
```

Three independent processes, each with its own lifecycle:

| Component  | What it is                          | Role                                          |
|------------|-------------------------------------|-----------------------------------------------|
| `rtfm`     | Console CLI                         | Converts `.docx` → markdown, chunks, indexes  |
| OpenSearch | Single-node container (Docker)      | Persistent search store (`rtfm-docs` index)   |
| `rtfm-mcp` | stdio MCP server                    | Exposes `search_docs` to the LLM client       |

The CLI converts each `.docx` (Mammoth → ReverseMarkdown), splits it into
heading-aware chunks with breadcrumb context, and bulk-indexes them. Retrieval
is **hybrid**: a tuned BM25 lexical search (excellent for technical lookups)
fused with local in-process semantic embeddings (all-MiniLM-L6-v2 via ONNX
Runtime — auto-downloaded once, ~90 MB, cached per user; without it search
degrades gracefully to lexical-only). See [`CLAUDE.md`](./CLAUDE.md) for the
full design and rationale.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [Docker](https://www.docker.com/) (Docker Desktop on macOS/Windows; on native
  Linux also set `vm.max_map_count=262144` so OpenSearch will start)
- An MCP-capable LLM client (e.g. Claude Code)

Runs on Windows, macOS, and Linux.

## Getting started

```bash
# 1. Start the local search store
docker compose up -d

# 2. Build
dotnet build -c Release

# 3. Confirm the CLI can reach OpenSearch
dotnet run --project src/Rtfm.Cli -- ping

# 4. Index your documentation (first run downloads the embedding model, ~90 MB)
dotnet run --project src/Rtfm.Cli -- index ./docs

# 5. Search it from the CLI
dotnet run --project src/Rtfm.Cli -- search "how are roles mapped to functions"

# 6. (optional) Keep the index fresh as docs change
dotnet run --project src/Rtfm.Cli -- watch ./docs
```

### Inspecting the index (optional)

For a visual look at the index while debugging, start OpenSearch Dashboards via
the `debug` compose profile (kept out of the default `up` so the core stack
stays lean):

```bash
docker compose --profile debug up -d      # → http://localhost:5601
```

### Wiring into Claude Code

The MCP server is registered as a project-scoped server via the committed
[`.mcp.json`](./.mcp.json) at the repo root, so every developer gets it on clone.
It exposes one tool, `search_docs(query, top_k, project?)`, returning ranked
passages with their project, source, heading breadcrumb, last-modified date, and
text. Scope is set by the `RTFM_PROJECT` env var in `.mcp.json` (omit or pass
`project="*"` to search across all projects).

Build in Release first (the config points at the built DLL, not `dotnet run`),
then use `/mcp` in Claude Code to confirm `rtfm` connected and see its tools.
Editing `.mcp.json` needs a Claude Code restart to take effect.

### Using RTFM from your other repos

RTFM is designed to serve **many codebases at once**: OpenSearch and its index
are shared, each Claude Code instance spawns its own `rtfm-mcp` process, and the
per-chunk `project` field keeps corpora from blurring together.

One-time, per machine:

```bash
# 1. Point RTFM_HOME at your rtfm clone (set it as a persistent user env var)
#    PowerShell:  [Environment]::SetEnvironmentVariable('RTFM_HOME', 'D:\Projects\rtfm', 'User')
#    bash/zsh:    export RTFM_HOME=~/src/rtfm   (in your shell profile)

# 2. Index each project's docs under its own project name
rtfm index D:\docs\payments --project payments
rtfm index D:\docs\pam      --project pam
```

Then drop this `.mcp.json` into each consuming repo — it is the same template
everywhere; only `RTFM_PROJECT` changes:

```json
{
  "mcpServers": {
    "rtfm": {
      "command": "dotnet",
      "args": ["${RTFM_HOME:-.}/src/Rtfm.Mcp/bin/Release/net10.0/Rtfm.Mcp.dll"],
      "env": {
        "RTFM_OPENSEARCH_URL": "http://localhost:9200",
        "RTFM_PROJECT": "payments"
      }
    }
  }
}
```

Notes:

- Claude Code expands `${RTFM_HOME:-.}` at launch; the `.-` fallback makes the
  same file work inside the rtfm repo itself (where the path is relative to the
  repo root). Committing this file is safe — each dev supplies their own
  `RTFM_HOME`.
- Claude Code prompts for approval the first time each repo uses the server —
  expected, not a bug.
- Sessions auto-scope to their repo's `RTFM_PROJECT`; the agent can still pass
  `project="*"` to compare across projects, and every hit carries its `project`
  for attribution.
- Alternatively, `claude mcp add --scope user rtfm …` registers the server once
  for your user across all repos — but then there's no per-repo scoping; you
  trade the committed template for "all projects unless the agent filters".
- Each running instance loads its own embedding model (~100–200 MB RAM) on
  first search, and holds a lock on the built DLL — disconnect instances (or
  restart Claude Code) before rebuilding rtfm.

## Documentation

- [`CLAUDE.md`](./CLAUDE.md) — architecture, locked design decisions, tech stack,
  and the phased development plan.

## License

[MIT](./LICENSE)
