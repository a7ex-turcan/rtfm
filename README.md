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
ships in tiers: **Tier 1** is a tuned BM25 lexical search (excellent for
technical lookups); **Tier 2** adds local in-process embeddings for hybrid
semantic search. See [`CLAUDE.md`](./CLAUDE.md) for the full design and rationale.

## Requirements

- [.NET 10 SDK](https://dotnet.microsoft.com/)
- [Docker](https://www.docker.com/) (Docker Desktop on macOS/Windows; on native
  Linux also set `vm.max_map_count=262144` so OpenSearch will start)
- An MCP-capable LLM client (e.g. Claude Code)

Runs on Windows, macOS, and Linux.

## Getting started

> **Status:** early development — the solution is being scaffolded. The commands
> below describe the intended workflow; not all are wired up yet.

```bash
# 1. Start the local search store
docker compose up -d

# 2. Build
dotnet build -c Release

# 3. Index your documentation
dotnet run --project src/Rtfm.Cli -- index ./docs

# 4. (optional) Keep the index fresh as docs change
dotnet run --project src/Rtfm.Cli -- watch ./docs
```

### Wiring into Claude Code

The MCP server is registered as a project-scoped server via the committed
[`.mcp.json`](./.mcp.json) at the repo root, so every developer gets it on clone.
Build in Release first (the config points at the built DLL, not `dotnet run`),
then use `/mcp` in Claude Code to confirm `rtfm` connected and see its tools.

## Documentation

- [`CLAUDE.md`](./CLAUDE.md) — architecture, locked design decisions, tech stack,
  and the phased development plan.

## License

[MIT](./LICENSE)
