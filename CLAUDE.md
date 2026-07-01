# RTFM

> **R**etrieval **T**ool **F**or **M**anuals.
> (The team knows what it really stands for. The README does not.)

A local, per-developer documentation retrieval tool. It indexes a folder of
Confluence-exported `.docx` documentation into a local OpenSearch instance and
exposes it to any MCP-capable LLM client (Claude Code, Claude Desktop, IDE
integrations) over a stdio MCP server. Instead of manually attaching docs to a
chat, a developer asks their LLM and it retrieves the relevant passages itself.

The tool answers two kinds of question well:

- **Technical lookup** — "What's the endpoint to GET this resource?" (lexical;
  the user's words appear verbatim in the docs)
- **Conceptual** — "What does Bundle mean?" (semantic; the answer may never
  repeat the user's phrasing)

---

## 1. Architecture

Three independent components. Keep them decoupled — this separation is a
deliberate decision, not an accident (see §2).

```
        ┌─────────────────────────────────────────────────────────┐
        │  docs/  (Confluence-exported .docx, mostly static)        │
        └───────────────────────────┬─────────────────────────────┘
                                     │  read
                                     ▼
        ┌─────────────────────────────────────────────────────────┐
        │  rtfm  (console CLI, long-lived in watch mode)            │
        │   ├─ convert:  Mammoth (.docx→HTML) → ReverseMarkdown     │
        │   ├─ chunk:    heading-aware, breadcrumb + overlap        │
        │   └─ index:    bulk upsert / delete into OpenSearch       │
        └───────────────────────────┬─────────────────────────────┘
                                     │  HTTP (bulk, delete-by-query)
                                     ▼
        ┌─────────────────────────────────────────────────────────┐
        │  OpenSearch  (single-node, Docker, persistent volume)     │
        │   index: rtfm-docs  (keyword + text + knn_vector fields)  │
        └───────────────────────────┬─────────────────────────────┘
                                     │  HTTP (search)
                                     ▼
        ┌─────────────────────────────────────────────────────────┐
        │  rtfm-mcp  (stdio MCP server)                             │
        │   tool: search_docs(query, top_k)  → ranked chunks        │
        └───────────────────────────┬─────────────────────────────┘
                                     │  stdio (MCP)
                                     ▼
        ┌─────────────────────────────────────────────────────────┐
        │  LLM client  (Claude Code / Claude Desktop / IDE)         │
        └─────────────────────────────────────────────────────────┘
```

**Why three pieces and not one:** OpenSearch must persist independently of any
client session. The watcher must run continuously to keep the index fresh. The
MCP server only lives for as long as the LLM client that spawned it — so it
cannot host the watcher. Each piece has a different lifecycle, so each is its
own process.

---

## 2. Decisions

Decisions already made and locked, with the reasoning, so we don't relitigate
them mid-build.

### 2.1 Platform: .NET throughout
Matches team skills (.NET-first shop). The whole thing — CLI, conversion,
indexing, MCP server — is C#. No polyglot, no per-developer toolchain sprawl.
Target **.NET 10** (LTS; ships the MCP server project template).

**Solution file: use the `.slnx` (XML) format, not the legacy `.sln`.** The
solution is `Rtfm.slnx`. Create it with `dotnet new sln --format slnx` and add
projects with `dotnet sln Rtfm.slnx add …`.

### 2.2 MCP server: official C# SDK, stdio transport
Use the official `ModelContextProtocol` NuGet package (the
`AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly()` pattern;
tools are plain methods tagged `[McpServerTool]`). **stdio**, not HTTP — the
client spawns the server as a child process locally. No ports, no auth, no CORS.

> **stdout is sacred (footgun).** A stdio MCP server must write *only* MCP
> protocol messages to stdout. Any stray `Console.WriteLine`, build output, or
> default-console log corrupts the transport and the server silently fails to
> connect. **All logging goes to stderr** — configure the host with
> `LogToStandardErrorThreshold = LogLevel.Trace` (as in the SDK's own sample).
> This is the single most common reason a .NET stdio server "won't connect."

### 2.3 Store: local single-node OpenSearch in Docker
One container, one persistent volume, security plugin disabled for local use.
No cluster, no auth headaches. Shipped via `docker-compose.yml` so a dev runs
`docker compose up -d` once.
- **Non-goal:** .NET Aspire orchestration. It's a single container per dev;
  Aspire is overkill here and adds a dependency. Plain compose.

### 2.4 Corpus is static → batch index + watch
The doc set changes only occasionally. So:
- `rtfm index ./docs` — one-shot full (re)index. The common path.
- `rtfm watch ./docs` — long-running, keeps the index fresh on change.

No live pipeline / CDC / TTL machinery. See §2.8 for watch-mode specifics.

### 2.5 Conversion: in-process, Route A (Mammoth → ReverseMarkdown)
**Decision: Option A.** Convert `.docx` entirely in-process — no Pandoc, no
native binary, no PATH problems for per-dev distribution.

Pipeline: **Mammoth (.NET)** converts `.docx` → semantic HTML using the
document's *style names* (a paragraph styled `Heading 1` becomes `<h1>`), then
**ReverseMarkdown** converts that HTML → markdown. Mammoth's own direct-to-
markdown output is deprecated; the maintainer recommends HTML + a separate
markdown converter, which yields better results.

**Known caveats (both are real, plan around them):**
- **Style-driven dependency.** Mammoth keys off real heading styles. If
  Confluence authors *manually bolded* text instead of using Heading styles,
  the hierarchy collapses to flat paragraphs. → **Spot-check real exports
  early (Phase 1).** If headings are faked, we need a heuristic pass (detect
  bold-only "heading-like" lines) or we accept flatter chunks.
- **Tables.** Simple parameter/endpoint tables round-trip into markdown pipe
  tables fine. Merged-cell / nested tables can scramble. Tables carry the
  *technical* answers, so this matters. → If specific docs mangle, drop to the
  **Open XML SDK** (`DocumentFormat.OpenXml`) *for table extraction only* and
  let Mammoth handle prose (hybrid converter). Don't pre-build this; add it if
  Phase 1 spot-checks show we need it.

### 2.6 Strip Confluence boilerplate
Exports carry page footers ("Created by… last modified…"), breadcrumbs, and
macro residue. Filter these before indexing or they pollute both lexical and
semantic matches.

### 2.7 Chunking: heading-aware with breadcrumb + overlap
Chunk quality is the single biggest driver of retrieval quality — more than the
search tech. Rules:
- Split on heading boundaries, not fixed character counts.
- Prepend the **heading path / breadcrumb** to every chunk
  (e.g. `API > Documents > GET /documents/{id}`). This gives the LLM context
  regardless of whether the chunk was found by keyword or by vector, and it's
  cheap leverage that helps *both* question types.
- Add modest overlap so answers spanning a boundary aren't lost.
- Store metadata on every chunk: **source file path** (see §2.9) and
  **heading path**.

### 2.8 Watch mode: FileSystemWatcher, handled correctly
`System.IO.FileSystemWatcher` is the mechanism, but the naive version
misbehaves. Required handling:
- **Debounce** — one save fires multiple events; coalesce per-path over a
  ~500ms window before acting (a `ConcurrentDictionary<path, ChangeKind>`
  drained on a timer).
- **File locks** — `Changed` often fires while the editor is still writing;
  opening immediately throws `IOException`. Retry with backoff (the debounce
  window absorbs most of this).
- **Deletes & renames** — `Deleted` → remove the doc's chunks from OpenSearch;
  `Renamed` → delete old path's chunks + index the new path. Re-index alone
  only covers Created/Changed.
- **Startup reconcile** — the watcher only sees changes *from launch onward*.
  On start, scan the folder and compare against a stored manifest of
  (path → last-write / content hash); catch up on anything edited while the
  watcher was off, then enter watch mode. This is the difference between
  "reliable" and "mostly works."

### 2.9 Source path metadata + delete-by-query
Every chunk stores its source file path as a **`keyword`** field. Updates and
deletes are `delete-by-query` on that exact-match field, then re-index. Without
this, stale chunks can't be cleaned up. Match on the `keyword` field, never the
analyzed text field. **The stored path must be normalized** (separator + casing)
so exact-match holds across platforms — see §2.12.

### 2.10 Retrieval: hybrid, built hybrid-ready from day one — shipped in tiers
The mixed question profile (§intro) is the textbook case *for* hybrid:
lexical wins technical lookups, semantic wins conceptual questions. Running only
one is good at half the questions.

**The index mapping is hybrid-ready from the start** (keyword + text + knn_vector
fields all present), but we ship in two tiers so technical lookup works on day
one without the embedding weight:

- **Tier 1 (ship first) — smart BM25.**
  - A `keyword` sub-field for exact tokens (endpoint paths, resource names) and
    an analyzed `text` field for prose; query both.
  - A **custom analyzer** that doesn't choke on technical tokens — preserve `/`,
    `_`, and camelCase so `GET /Bundle` and `getUserAccessKeys` stay
    searchable.
  - Result: technical lookups excellent, conceptual "okay".

- **Tier 2 (flip on later) — add semantic.**
  - Populate the `knn_vector` field with embeddings on ingest, run **hybrid
    (BM25 + kNN)** combined with **RRF** (or score normalization) via an
    OpenSearch search pipeline. Confirm the exact hybrid/normalization wiring
    against the running OpenSearch version.
  - Embeddings run **locally, in-process** (e.g. a small model via ONNX
    Runtime) — no external API, consistent with per-dev offline-ish use.
  - Because the mapping already has the vector field, turning this on is a
    **backfill**, not a structural reindex.

> **OpenSearch .NET client note:** prefer low-level / raw JSON queries for the
> hybrid + analyzer config rather than fighting the strongly-typed client where
> it's awkward (known constructor-parameter constraints on some typed
> operations). Raw query bodies are acceptable and clearer here.

### 2.11 MCP tool surface (MVP)
One tool to start:
- `search_docs(query: string, top_k: int = 5)` → ranked chunks, each with its
  heading breadcrumb, source file, and text.

Possible later additions (don't build yet): `get_document(path)` to fetch a full
page, `list_sources()` to enumerate indexed docs.

### 2.12 Cross-platform (Windows, macOS, Linux)
The tool must run on all three. The stack is portable for free (.NET, Docker'd
OpenSearch, and every library are cross-platform), so this section is about the
handful of places an agent will otherwise hardcode OS-specific behavior:

- **Paths in code:** always `Path.Combine` / `Path.DirectorySeparatorChar`.
  Never string-concatenate with `\` or `/`, never assume a separator.
- **Stored source-path key (critical — see §2.9):** the source path is a
  `keyword` field used for exact-match delete-by-query. Windows paths are
  **case-insensitive**, Linux/macOS are **case-sensitive**, and separators
  differ. If the indexed key and a later watcher event disagree on casing or
  slash direction, delete/rename silently misses and stale chunks leak. →
  **Normalize the path before storing it as the key**: consistent forward-slash
  separator and a single documented casing rule, applied identically on index,
  delete, and rename. This normalization is the one cross-platform bug that
  corrupts data rather than just erroring.
- **FileSystemWatcher differs per OS** (inotify / FSEvents / Win32) in rename
  behavior and event count per save. The debounce design (§2.8) absorbs most of
  it, but **watch mode must be validated on each target OS**, not only the
  author's laptop.
- **OpenSearch on native Linux** requires the host setting
  `vm.max_map_count=262144` or the container won't start. Docker Desktop
  (Mac/Windows) sets this inside its VM automatically; native Linux hosts do
  not. Document this in the compose/setup notes.
- **`.mcp.json` uses `dotnet <dll>`, not a native exe** — this is the portable
  choice and runs anywhere `dotnet` is on PATH (§6). Forward slashes in the args
  path are fine; the CLR normalizes them. Don't swap it for a per-OS binary.

---

## 3. Tech stack / dependencies

| Concern | Choice |
|---|---|
| Runtime | .NET 10 |
| MCP server | `ModelContextProtocol` (+ `Microsoft.Extensions.Hosting`) |
| docx → HTML | `Mammoth` |
| HTML → markdown | `ReverseMarkdown` |
| Tables fallback (only if needed) | `DocumentFormat.OpenXml` |
| Search store | OpenSearch (single-node, Docker) |
| OpenSearch client | official `opensearch-net` (low-level where typed client is awkward) |
| Embeddings (Tier 2) | local ONNX Runtime, small embedding model |
| File watching | `System.IO.FileSystemWatcher` |

> Pin exact package versions at build time — verify latest stable against NuGet
> when scaffolding.
>
> **Pinned so far (Phase 0):** `OpenSearch.Net` **1.8.0**; OpenSearch Docker
> image **2.17.1**. Bump deliberately, not automatically.

---

## 4. Suggested solution layout

```
rtfm/
├─ CLAUDE.md
├─ docker-compose.yml            # single-node OpenSearch + volume
├─ Rtfm.slnx                     # XML solution format (slnx, not legacy .sln)
├─ src/
│  ├─ Rtfm.Core/                 # shared: conversion, chunking, OpenSearch access
│  │  ├─ Configuration/          # environment/config resolution (RtfmEnvironment)
│  │  ├─ OpenSearch/             # connection + cluster health (gateway, low-level client)
│  │  ├─ Conversion/             # Mammoth + ReverseMarkdown pipeline, boilerplate strip
│  │  ├─ Chunking/               # heading-aware chunker, breadcrumb builder
│  │  ├─ Indexing/               # mapping, bulk upsert, delete-by-query
│  │  ├─ Search/                 # query builders (Tier 1 BM25 now, hybrid later)
│  │  └─ Manifest/               # startup-reconcile manifest (path → hash/mtime)
│  ├─ Rtfm.Cli/                  # `rtfm index` / `rtfm watch`  (console)
│  └─ Rtfm.Mcp/                  # `rtfm-mcp` stdio MCP server (search_docs)
└─ tests/
   └─ Rtfm.Core.Tests/           # conversion + chunking fixtures from real exports
```

Both executables (`Rtfm.Cli`, `Rtfm.Mcp`) depend on `Rtfm.Core`. The conversion,
chunking, and search logic lives in Core and is shared.

---

## 5. Development plan

Phased so each milestone is independently verifiable. Each phase has a
"**Done when**" acceptance criterion to build against.

### Phase 0 — Scaffold & infra ✅ **Done**
Solution structure, `docker-compose.yml` for single-node OpenSearch, a thin
health-check command (`rtfm ping`) that confirms connectivity to OpenSearch.
**Done when:** `docker compose up -d` + `rtfm ping` reports a healthy cluster.

*Delivered:* `Rtfm.slnx` + the four projects; `RtfmEnvironment` (resolves
`RTFM_OPENSEARCH_URL`) and `OpenSearchGateway.PingAsync` in Core; the `rtfm ping`
command; compose with a single-node cluster (security disabled, persistent
volume, healthcheck). `Rtfm.Mcp` is a stderr-only placeholder until Phase 4. The
CLI dispatches subcommands with a plain `switch` (no System.CommandLine yet —
adopt it if the command surface grows).

### Phase 1 — Conversion pipeline
Mammoth → ReverseMarkdown in `Rtfm.Core/Conversion`. Boilerplate stripping
(footers, breadcrumbs, macro residue). **Spot-check against several real
Confluence exports**, especially heading fidelity and tables.
**Done when:** a representative `.docx` converts to clean markdown with headings
preserved and simple tables intact; the heading-style caveat (§2.5) is confirmed
true/false for our actual corpus. *If headings are faked or tables mangle, log
it and decide on the heuristic / Open XML fallback before moving on.*

### Phase 2 — Chunking
Heading-aware splitter, breadcrumb prefixing, overlap, metadata
(source path + heading path).
**Done when:** a converted doc yields chunks that each carry a correct
breadcrumb and source path, with sane sizes and overlap.

### Phase 3 — Indexing (batch)
OpenSearch index mapping: `keyword` + analyzed `text` + custom analyzer
(preserve `/`, `_`, camelCase) + a `knn_vector` field defined but unused.
Bulk upsert. `rtfm index ./docs`.
**Done when:** `rtfm index ./docs` populates `rtfm-docs` and a manual query
returns sensible hits for a known term.

### Phase 4 — MCP server (Tier 1 retrieval)
stdio server exposing `search_docs(query, top_k)`. Tier 1 BM25 query across
keyword + text. Returns chunks with breadcrumb + source. Wire into Claude Code
via project-scoped `.mcp.json` (§6).
**Done when:** from inside Claude Code, asking "what's the endpoint to GET X"
retrieves the right passage via the tool.

### Phase 5 — Watch mode
`rtfm watch ./docs` — FileSystemWatcher with debounce, lock retry, delete/rename
handling, and startup reconcile against the manifest (§2.8).
**Done when:** editing, adding, renaming, and deleting a doc are each reflected
in search results within seconds, and a change made while the watcher was off is
caught up on next start.

### Phase 6 — Semantic tier (deferred)
Local in-process embeddings, backfill `knn_vector`, hybrid BM25 + kNN with RRF /
normalization via search pipeline. Validate the *conceptual* question half.
**Done when:** "what does Bundle mean" returns the defining passage even when the
query shares few exact words with the doc — without regressing Tier 1 technical
lookups.

---

## 6. Wiring into Claude Code (`.mcp.json`)

Once `Rtfm.Mcp` builds, register it as a **project-scoped** MCP server so it's
committed to the repo and every dev gets it automatically on clone. Create
`.mcp.json` at the repo root:

```json
{
  "mcpServers": {
    "rtfm": {
      "command": "dotnet",
      "args": ["src/Rtfm.Mcp/bin/Release/net10.0/Rtfm.Mcp.dll"],
      "env": {
        "RTFM_OPENSEARCH_URL": "http://localhost:9200"
      }
    }
  }
}
```

Notes on this:
- **Build first.** Point at the built DLL, **not** `dotnet run` — `dotnet run`
  can emit build/restore output to stdout and corrupt the stdio transport (see
  §2.2). Run `dotnet build -c Release` before the first connect. A published
  single-file exe is the cleaner long-term answer for team distribution.
- **No secrets.** This file is committed. Local OpenSearch has no auth today; if
  that ever changes, reference a per-dev env var rather than hardcoding.
- **Approval + restart.** Claude Code prompts for approval before using a
  project-scoped server on first run (expected, not a bug). Edits to `.mcp.json`
  don't affect a running session — restart Claude Code or use `/mcp` to
  reconnect. `/mcp` is also the status panel to confirm `rtfm` connected and see
  its tools.
- **Self-test harness.** With this in place, Claude Code can call `search_docs`
  against the very docs being indexed while building — the tool tests itself.
- **Don't name a server `workspace`** — that name is reserved by Claude Code and
  will be silently skipped.

---

## 7. Notes for Claude Code

- Keep `Rtfm.Core` free of CLI/MCP concerns — both executables consume it.
- **`Rtfm.Mcp` logs to stderr only, never stdout** (§2.2). One stray
  `Console.WriteLine` breaks the MCP transport. Configure the host accordingly
  and keep all diagnostics on stderr.
- Conversion and chunking are the quality-critical paths; **back them with
  fixture tests using real (sanitized) Confluence exports**, not synthetic docs.
- Don't build Tier 2 (embeddings/hybrid) or the Open XML table fallback until
  their triggering conditions are actually hit. Resist scope creep — the joke is
  that the answer was in the docs all along, not that we boiled the ocean.
- Prefer raw OpenSearch query bodies where the typed client is constraining.
- **Commits: no self-attribution.** Do not add `Co-Authored-By: Claude`
  trailers, "Generated with Claude Code" lines, or any similar attribution to
  commit messages, PR descriptions, or code comments. Write commit messages as
  the developer, nothing more.
- Domain note: the docs appear to be FHIR-flavored healthcare API material
  (e.g. "Bundle"), which is exactly the mixed lexical/conceptual profile this
  design targets.
