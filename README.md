# RTFM

**R**etrieval **T**ool **F**or **M**anuals — a local, per-developer documentation
search tool for your LLM.

RTFM indexes a folder of documentation — Confluence exports, Word, Markdown,
PDF, Excel, CSV — into a local
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
docs/          ──►  rtfm (CLI)  ──►  OpenSearch  ──►  rtfm-mcp  ──►  your LLM
(.doc .docx .md     convert · chunk     rtfm-docs      search_docs       client
 .pdf .xlsx .csv)   · index               index          + 3 more (MCP)
```

Three independent processes, each with its own lifecycle:

| Component  | What it is                          | Role                                          |
|------------|-------------------------------------|-----------------------------------------------|
| `rtfm`     | Console CLI                         | Converts documents → markdown, chunks, indexes |
| OpenSearch | Single-node container (Docker)      | Persistent search store (`rtfm-docs` index)   |
| `rtfm-mcp` | stdio MCP server                    | Exposes `search_docs` & friends to the LLM client |

The CLI converts each document to markdown, splits it into
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

## CLI reference

`rtfm` with no arguments (or `--help`) prints this overview in the terminal.

| Command | Arguments | What it does |
|---|---|---|
| `rtfm ping` | — | Health-checks the OpenSearch cluster; color-coded status panel. |
| `rtfm index` | `<folder> [--project <name>]` | One-shot (re)index of every supported document under `<folder>` — convert → chunk → embed → bulk upsert. Idempotent: re-running replaces each doc's chunks in place. Writes the watch manifest so `watch` starts from a correct baseline. Default project: `default`. |
| `rtfm watch` | `<folder> [--project <name>]` | Long-running incremental indexer. On start it *reconciles*: anything added/changed/deleted while the watcher was off is caught up. Then edits, adds, renames, and deletes are reflected in the index within seconds (debounced, editor-lock tolerant). Live dashboard on a terminal; plain log lines when redirected. `Ctrl+C` to stop. |
| `rtfm search` | `<query...> [--project <name> \| --all]` | Hybrid search (BM25 + semantic kNN, fused). Top 5 hits as ranked cards with score bar, heading breadcrumb, source file, project, and last-modified date. No flag or `--all` spans all projects. |
| `rtfm purge` | `<project> [--yes]` | Removes **everything** for one project: its chunks in OpenSearch and its watch manifests. Shows what's on the block and asks first; `--yes` skips the prompt (and is required when output is redirected). Other projects are untouched. |
| `rtfm convert` | `<path>` | Dev aid: converts one document to markdown on stdout (pipe-friendly, no styling). |
| `rtfm chunk` | `<path>` | Dev aid: converts, then prints the heading-aware chunks with their breadcrumbs. |

**Supported document formats** (detected by content, not extension): Confluence
"Export to Word" files (`.doc` — actually MHTML), genuine Word `.docx`,
Markdown (`.md`/`.markdown`), PDF (headings inferred from font sizes — expect
flatter structure than Word exports), Excel `.xlsx` (each sheet becomes a
section with its data as a table), and CSV (one table, header row preserved
across chunk splits).

**Projects.** Every chunk is tagged with the `--project` it was indexed under
(default `default`); search and the MCP server filter on it. A file belongs to
one project at a time — re-indexing it under a new name moves it.

**Output conventions.** Results go to stdout, diagnostics and progress to
stderr — so piping or redirecting a command yields only the machine-usable
part, plain and colorless. Exit codes: `0` success, `1` failure, `2` usage
error.

**Semantic tier.** The first `index`/`search`/`watch` run downloads the local
embedding model (~90 MB, once per machine, cached under
`LocalApplicationData/rtfm/models`). If the model can't be fetched (offline),
commands warn and continue lexical-only — nothing breaks, conceptual queries
just get weaker until the model is available.

| Environment variable | Meaning |
|---|---|
| `RTFM_OPENSEARCH_URL` | OpenSearch endpoint (default `http://localhost:9200`) |
| `RTFM_PROJECT` | Default project scope for the MCP server (per-call `project` argument overrides; `*` = all) |
| `RTFM_MODEL_DIR` | Embedding-model cache override (e.g. an offline pre-provisioned copy) |

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
It exposes four tools:

| Tool | Purpose |
|---|---|
| `search_docs(query, top_k, project?)` | Ranked passages (hybrid lexical + semantic) with project, source, path, breadcrumb, last-modified date, text |
| `get_document(path, project?)` | One full document as markdown, reassembled from its chunks — for answers that sprawl past a single passage |
| `list_sources(project?)` | Every indexed doc with title, project, date, chunk count — corpus awareness ("do the docs even cover this?") |
| `find_similar(path, top_k, project?)` | Semantically related documents, with the best-matching section as the "why" |

Scope for all tools is set by the `RTFM_PROJECT` env var in `.mcp.json` (omit or
pass `project="*"` to search across all projects). Path arguments accept the
full `path` from other tools' results or a bare filename.

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
