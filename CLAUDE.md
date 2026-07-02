# RTFM

> **R**etrieval **T**ool **F**or **M**anuals.
> (The team knows what it really stands for. The README does not.)

A local, per-developer documentation retrieval tool. It indexes a folder of
documentation — Confluence MHTML exports (`.doc`), Word `.docx`, plain
Markdown (`.md`), PDF, Excel (`.xlsx`), CSV, and draw.io diagrams — into a
local OpenSearch instance and exposes it to any
MCP-capable LLM client (Claude Code, Claude Desktop, IDE integrations) over a
stdio MCP server. Instead of manually attaching docs to a chat, a developer asks
their LLM and it retrieves the relevant passages itself.

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
        │  docs/  (MHTML/.doc, .docx, .md — mostly static)          │
        └───────────────────────────┬─────────────────────────────┘
                                     │  read
                                     ▼
        ┌─────────────────────────────────────────────────────────┐
        │  rtfm  (console CLI, long-lived in watch mode)            │
        │   ├─ convert:  MHTML/docx/md → HTML → ReverseMarkdown     │
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
`docker compose up -d` once — or `rtfm init [--with-model]`, which runs compose
(`up -d --wait` against the healthcheck), verifies connectivity, and creates
the index + hybrid pipeline in one shot. The compose file is also *embedded* in
`Rtfm.Cli` (linked from the repo root — one source of truth) and materialized
under `LocalApplicationData/rtfm/` when init runs outside the repo; resolution
order is cwd → `RTFM_HOME` → embedded copy. That embedded copy is what lets a
packaged, repo-less `rtfm` (Phase 14) still bootstrap a machine.
- **Non-goal:** .NET Aspire orchestration. It's a single container per dev;
  Aspire is overkill here and adds a dependency. Plain compose.
- **Optional debug UI:** OpenSearch Dashboards is wired into `docker-compose.yml`
  behind a `debug` profile (`docker compose --profile debug up -d` →
  `http://localhost:5601`), version-matched with security disabled. It is a
  debugging aid only — deliberately *not* part of the default `up`, and not a
  human-facing UX for RTFM (the LLM client is the UX, §2.11). A purpose-built
  dashboard is only worth revisiting for §2.13 C contradiction curation.

### 2.4 Corpus is static → batch index + watch
The doc set changes only occasionally. So:
- `rtfm index ./docs` — one-shot full (re)index. The common path.
- `rtfm watch ./docs` — long-running, keeps the index fresh on change.

No live pipeline / CDC / TTL machinery. See §2.8 for watch-mode specifics.

### 2.5 Conversion: in-process, multi-format → markdown
**Decision: convert everything in-process — no Pandoc, no native binary, no
PATH problems for per-dev distribution.** Three input formats shipped first
(in this order; Phase 9 later added PDF, xlsx, and CSV as additional
front ends into the same tail):

1. **MHTML** (`.doc`) — Confluence's "Export to Word" output. *Despite the
   `.doc` extension these are **not** Word files at all: they are MIME
   `multipart/related` documents wrapping a **quoted-printable** `text/html`
   part plus base64 image parts.* (Confirmed against the real corpus — see the
   finding below.) **Ship first** — this is what the sample corpus actually is.
2. **`.docx`** — genuine Word / Open XML. Ship second.
3. **`.md`** — plain Markdown. Ship third; near-trivial passthrough.

**Route by sniffing content, not just the extension** — the sample files prove
the extension lies (a `.doc` that is really MHTML). Detection order:
- zip magic `PK\x03\x04` → **docx**
- MIME headers (`MIME-Version:` / `multipart/related`) → **MHTML**
- otherwise, `.md` extension → **markdown passthrough**

**Per-format front end, shared back end.** Each route produces HTML (markdown
skips straight to the end), then a **common tail** strips boilerplate (§2.6) and
runs **ReverseMarkdown** (HTML → markdown). Chunking (Phase 2) is
format-agnostic: it only ever sees markdown + a heading path.

- **MHTML route:** **MimeKit** parses the container, selects the `text/html`
  part and decodes quoted-printable → shared strip + **ReverseMarkdown**.
- **docx route:** **Mammoth (.NET)** converts `.docx` → semantic HTML using the
  document's *style names* (a paragraph styled `Heading 1` becomes `<h1>`) →
  same tail. Mammoth's own direct-to-markdown output is deprecated; HTML + a
  separate markdown converter yields better results.
- **markdown route:** already markdown — passthrough with only light
  normalization; no HTML stage.

> **Real-corpus finding (Phase 1 spot-check — done):** the five sample exports
> in `docs/` are all MHTML with **real `<h1>`/`<h2>`/`<h3>` heading tags**, so
> the "faked headings" risk below is **false for this corpus** — heading-aware
> chunking will work. Tables are standard HTML (`confluenceTable`) with some
> **colspan** (3–7 per file) and **no rowspan**.

**Known caveats (plan around them):**
- **Style-driven headings (docx route only).** Mammoth keys off real heading
  styles. If a `.docx` author *manually bolded* text instead of using Heading
  styles, the hierarchy collapses to flat paragraphs. Spot-check `.docx` inputs
  when that route lands (a heuristic bold-line pass is the fallback). Does **not**
  affect MHTML, which carries real heading tags.
- **Tables.** Simple parameter/endpoint tables round-trip into markdown pipe
  tables fine. **colspan** can't be expressed in a pipe table (a merged cell
  collapses to one column with blanks alongside — usually still readable);
  merged/nested tables can scramble further. Tables carry the *technical*
  answers, so watch them. → For the **docx** route only, if specific docs mangle,
  drop to the **Open XML SDK** (`DocumentFormat.OpenXml`) *for table extraction
  only*. Don't pre-build it. (For MHTML the tables are already HTML, so any fix
  is a DOM-level pass, not Open XML.)

### 2.6 Strip boilerplate
Exports carry page footers ("Created by… last modified…"), breadcrumbs, and
macro residue. Filter these before indexing or they pollute both lexical and
semantic matches. Do it as a DOM pass (an HTML library) before ReverseMarkdown,
not with regexes on the markdown.

Concrete Confluence chrome seen in the real corpus (strip or unwrap these):
`jira-issue-key`, `icon`, `summary` (Jira macro tables), `inline-comment-marker`
(unwrap — keep the text), `external-link` link chrome, `table-wrap` /
`contentLayout2` / `Section1` layout wrappers, and the page footer block.

### 2.7 Chunking: heading-aware with breadcrumb + overlap
Chunk quality is the single biggest driver of retrieval quality — more than the
search tech. Rules:
- Split on heading boundaries, not fixed character counts.
- Prepend the **heading path / breadcrumb** to every chunk
  (e.g. `API > Documents > GET /documents/{id}`). This gives the LLM context
  regardless of whether the chunk was found by keyword or by vector, and it's
  cheap leverage that helps *both* question types.
- Add modest overlap so answers spanning a boundary aren't lost.
- Store metadata on every chunk: **source file path** (see §2.9), **heading
  path**, **timestamps** (`source_modified_at`, `indexed_at`; see §2.13), and
  **`project`** (see §2.14).

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
- `search_docs(query: string, top_k: int = 5, project?: string)` → ranked
  chunks, each with its heading breadcrumb, source file, text,
  `source_modified_at` (recency / contradiction reasoning — §2.13), and
  `project` (§2.14). Scope defaults to `RTFM_PROJECT`; the optional `project`
  arg overrides per call (a specific project, or all).

The tool surface is now eight tools: `search_docs`; the Phase 8 additions —
`get_document(path, project?)` (full reassembled page), `list_sources(project?)`
(corpus awareness), `find_similar(path, top_k?, project?)` (related docs via
chunk-vector centroid); `list_contradictions(project?, top_k?)` (Phase 12
nominations); and the Phase 13 correction tools — `add_note` (human-confirmed
only), `list_notes`, `remove_note`.

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

### 2.13 Knowledge recency & contradiction awareness
Docs evolve and disagree over time (an older page says `user-role = admin`, a
newer one says `super-admin`). RTFM should let the LLM reason about *which
knowledge is newer* and surface conflicts rather than silently averaging them.
Shipped in layers — **A + B are committed; C is deferred**.

- **(A) Timestamp every chunk.** Two `date` fields on each chunk:
  - `source_modified_at` — the document's own recency (the truth signal).
  - `indexed_at` — when RTFM ingested it (tie-breaker / audit).
  Source of the modified date, in priority order: embedded doc modified-date
  (docx core props `dcterms:modified`; Confluence "last modified" byline when
  present) → the MHTML `Date:` header → file mtime. These fields go into the
  mapping from the start (Phase 3) so enabling recency logic never needs a
  reindex.

- **(B) Recency-aware retrieval + conflict flagging (Phase 4).** `search_docs`
  returns each chunk's `source_modified_at` alongside source + breadcrumb, and
  the tool instructs the agent to treat the **newer** source as *likely*
  authoritative **and to flag contradictions to the user** rather than pick
  silently. Deliberately do **not** recency-boost-and-hide the older chunk —
  both must be retrieved for the conflict to be visible. "Newer = truth" is a
  strong heuristic, not a law (a new doc may be a scoped draft), so B *flags*,
  it does not overwrite.

- **(C) Agent-driven correction / supersession — RESOLVED (Phase 13): option 2,
  the overrides index.** Option 1 (write back to source) fell to a decisive
  observation: the "source" on disk is itself an *export* — the real source is
  Confluence, which we can't write, so source-corrections would be clobbered by
  the next export anyway. Option 3 remains the documented trap. Built as the
  `rtfm-notes` index: user-confirmed **override notes** (text, project,
  optional anchor path, author, timestamp, embedded vector), merged into
  retrieval at query time — matching notes join the candidate pool as
  attributed `origin: "note"` hits, and notes anchored to a document ride
  along as annotations on that document's hits. Human-in-the-loop: the agent
  proposes, the user confirms, only then `add_note`. Never auto-resolved,
  never masquerading as source text.

- **Proactive contradiction detection is a Tier-2 add-on.** Comparing a newly
  ingested chunk against *similar* existing chunks from other documents needs
  semantic similarity, so it waits for Tier-2 embeddings (§2.10). Query-time LLM
  reasoning (B) comes first.

### 2.14 Per-project segregation
A dev machine holds many projects, each with its own docs; the LLM must not blur
them together. Every chunk carries a **`project`** keyword field, set explicitly
at index time and used to scope retrieval. **Single shared index + filter**, not
index-per-project — trivial cross-project queries, one mapping, and "drop a
project" reuses delete-by-query (§2.9).

- **Index:** `rtfm index <folder> --project <name>` (default `"default"`). A
  file belongs to one project at a time; re-indexing it under a new name moves
  it.
- **Drop:** `rtfm purge <project> [--yes]` — the promised delete-by-query drop,
  plus removal of the project's watch manifests (a purge that left those would
  hand the next `rtfm watch` a stale reconcile baseline). Confirms
  interactively; refuses without `--yes` when non-interactive.
- **Consume (Phase 4):** an `RTFM_PROJECT` env var in `.mcp.json` sets the
  default scope. Because `.mcp.json` is already project-scoped and committed per
  repo (§6), each repo auto-scopes RTFM to its own project — near-zero ergonomic
  cost.
  - `RTFM_PROJECT=payments` → retrieval filtered to that project (the
    confusion-avoiding default).
  - unset/empty → retrieval spans **all** projects.
  - `search_docs` takes an optional `project` argument to override per call (a
    specific other project, or all) — the "compare across projects" path.
- **Provenance:** every hit carries its `project` (like `source_modified_at`),
  so even all-projects mode is unambiguous about which doc came from where.
- **Contradiction interplay (§2.13 B):** *within* a project, newer supersedes /
  flag conflicts; *across* projects, differences are **expected** — attribute
  them by project, do not flag them as contradictions.

---

## 3. Tech stack / dependencies

| Concern | Choice |
|---|---|
| Runtime | .NET 10 |
| MCP server | `ModelContextProtocol` (+ `Microsoft.Extensions.Hosting`) |
| MHTML (`.doc`) → HTML | `MimeKit` (MIME parse + quoted-printable decode) |
| docx → HTML | `Mammoth` |
| HTML DOM / boilerplate strip | `AngleSharp` |
| HTML → markdown | `ReverseMarkdown` |
| Markdown (`.md`) input | none — passthrough |
| PDF → markdown (Phase 9) | `PdfPig` (heading heuristics by font size) |
| xlsx → markdown (Phase 9) | `ClosedXML` (sheet sections + pipe tables) |
| CSV → markdown (Phase 9) | none — small built-in RFC 4180-ish parser |
| draw.io → markdown (Phase 15) | none — XLinq + DeflateStream (+ AngleSharp for labels) |
| OCR for PDF-embedded images (Phase 16) | `RapidOcrNet` (PP-OCRv5 via ONNX Runtime; models in-package) + SkiaSharp |
| Tables fallback (docx route only, if needed) | `DocumentFormat.OpenXml` |
| Search store | OpenSearch (single-node, Docker) |
| OpenSearch client | official `opensearch-net` (low-level where typed client is awkward) |
| Embeddings (Tier 2) | `Microsoft.ML.OnnxRuntime` + `Microsoft.ML.Tokenizers`, all-MiniLM-L6-v2 (384-dim, auto-downloaded + cached) |
| Reranker (Tier 3) | same runtime/tokenizer, ms-marco-MiniLM-L-6-v2 cross-encoder (max-window scoring) |
| File watching | `System.IO.FileSystemWatcher` |
| CLI presentation (`Rtfm.Cli` only — never `Rtfm.Mcp`) | `Spectre.Console` |

> Pin exact package versions at build time — verify latest stable against NuGet
> when scaffolding.
>
> **Pinned so far:** `OpenSearch.Net` **1.8.0**; OpenSearch Docker image
> **2.17.1** (Phase 0). `MimeKit` **4.17.0**, `AngleSharp` **1.5.1**,
> `ReverseMarkdown` **5.4.0** (Phase 1a), `Mammoth` **1.11.0** (Phase 1b),
> `ModelContextProtocol` **2.0.0-preview.1** + `Microsoft.Extensions.Hosting`
> **10.0.9** (Phase 4). `Microsoft.ML.OnnxRuntime` **1.27.0** +
> `Microsoft.ML.Tokenizers` **2.0.0** (Phase 6). `Spectre.Console` **0.57.1**
> (Phase 7). `PdfPig` **0.1.15** (NuGet ID `PdfPig`, *not* the stale
> `UglyToad.PdfPig`) + `ClosedXML` **0.105.0** (Phase 9). `RapidOcrNet`
> **2.0.0** (Phase 16 — the model-copy `None` items in Rtfm.Core.csproj are
> version-locked to this pin; bump both together). Bump deliberately, not
> automatically.

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

Each project carries its own `CLAUDE.md` with the project-local rules (Core:
host-agnosticism + load-bearing invariants; Cli: stream contract + Spectre
rules; Mcp: the stdout-is-sacred discipline; Tests: unit-only boundaries +
pinned contracts). This file stays the source of truth for cross-cutting
decisions; the per-project files hold what you must know *when editing there*.
Keep them in sync when a phase moves a boundary.

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

### Phase 1 — Conversion pipeline (multi-format) ✅ **Done**
In-process converters in `Rtfm.Core/Conversion`, dispatched by content sniffing
(§2.5) and sharing a boilerplate-strip + ReverseMarkdown tail:
- **1a — MHTML** (`MimeKit` → `AngleSharp` strip → `ReverseMarkdown`). ✅
- **1b — docx** (`Mammoth` → same tail). ✅
- **1c — markdown** (`.md` passthrough with light normalization). ✅

**Done when:** each representative input converts to clean markdown with headings
preserved and simple tables intact.

*Delivered:* `FormatDetector`, `MhtmlConverter`, `DocxConverter`,
`MarkdownConverter`, the shared `HtmlToMarkdownConverter` (boilerplate/attribute
strip + ReverseMarkdown + normalization), the `DocumentConverter` facade, and
the `rtfm convert <path>` dev command. MHTML validated against the five real
exports (h1–h3 preserved, clean tables → pipe tables); docx validated via a
minimal real OOXML fixture (Heading 1 → `#`); md is a normalizing passthrough.
**Known limitation:** table cells containing nested lists/paragraphs stay as
attribute-clean raw HTML (GitHub pipe tables can't hold block content) — the
text is still retrievable. Fixture tests are synthetic; the real corpus is
gitignored.

### Phase 2 — Chunking ✅ **Done**
Heading-aware splitter, breadcrumb prefixing, overlap, metadata
(source path + heading path).
**Done when:** a converted doc yields chunks that each carry a correct
breadcrumb and source path, with sane sizes and overlap.

*Delivered:* `MarkdownChunker` (+ `Chunk`/`ChunkMetadata`/`ChunkingOptions`) in
`Rtfm.Core/Chunking`, and the `rtfm chunk <path>` dev command. Splits on heading
boundaries into breadcrumb-tagged chunks; drops pure container headings (kept in
descendants' breadcrumbs); overlaps paragraph windows when a section exceeds the
size target; **splits oversized tables by rows, repeating the header** so each
piece is self-describing; respects code fences. Chunk carries source path,
title, and `source_modified_at` (§2.13 A); `indexed_at` is stamped at index time
(Phase 3). Validated on the real RBAC export: 25 chunks, correct nested
breadcrumbs, all within the size target.

### Phase 3 — Indexing (batch) ✅ **Done**
OpenSearch index mapping: `keyword` + analyzed `text` + custom analyzer
(preserve `/`, `_`, camelCase) + `date` fields (`source_modified_at`,
`indexed_at`; §2.13) + a `knn_vector` field defined but unused.
Bulk upsert. `rtfm index ./docs`.
**Done when:** `rtfm index ./docs` populates `rtfm-docs` and a manual query
returns sensible hits for a known term.

*Delivered:* `RtfmIndex` (mapping/settings as raw JSON; `rtfm_technical`
analyzer = whitespace + `word_delimiter_graph` with `preserve_original`, so
`BUSINESS_LINE__C` / `/Bundle` / camelCase stay searchable), `PathNormalizer`
(§2.12 key), timestamp extraction (§2.13 A: MHTML `Date` header → docx
`docProps/core.xml` → mtime fallback), `OpenSearchGateway` index/search ops
(raw JSON, low-level client), `DocumentIndexer` (per-doc delete-by-query + bulk
upsert, deterministic `_id` = `path#ordinal` → idempotent re-index), and
`DocumentSearch` (Tier 1 BM25 `multi_match`, optional `project` filter). CLI:
`rtfm index <folder> [--project]`, `rtfm search <query> [--project]`. Every chunk
also carries a **`project`** keyword (§2.14). Verified on the real corpus: 111
chunks / 5 docs, re-index stays 111, technical + conceptual queries return
sensible hits carrying `source_modified_at` and `project`; project filter scopes
correctly (pam → hits, other → 0, no flag → all).

### Phase 4 — MCP server (Tier 1 retrieval) ✅ **Done**
stdio server exposing `search_docs(query, top_k, project?)`. Tier 1 BM25 query
across keyword + text. Returns chunks with breadcrumb + source +
`source_modified_at` + `project`, scoped by `RTFM_PROJECT` (optional per-call
`project` override; §2.14), and instructs the agent to prefer newer sources and
flag contradictions within a project but attribute cross-project differences by
project (§2.13 B, §2.14). Wire into Claude Code via project-scoped `.mcp.json`
(§6).
**Done when:** from inside Claude Code, asking "what's the endpoint to GET X"
retrieves the right passage via the tool; and when two docs disagree, the newer
is preferred and the conflict is surfaced to the user.

*Delivered:* `Rtfm.Mcp` host (stdio transport, `WithToolsFromAssembly`, logging
pinned to stderr per §2.2) and `SearchDocsTool` (`DocumentSearch` injected via
DI). The tool `[Description]` carries the recency/contradiction + cross-project
guidance. `RtfmEnvironment.ResolveProjectScope` handles the `RTFM_PROJECT`
default / per-call override / `*`/`all`. `.mcp.json` registers the `rtfm`
server. Verified over raw stdio JSON-RPC (initialize / tools-list / tools-call):
advertises `search_docs`, `RTFM_PROJECT=pam` scopes results, each hit carries
`project` + `source_modified_at`, and **stdout stays pure JSON-RPC** (logs only
on stderr). Confirming *inside Claude Code* needs a restart to load `.mcp.json`
(approval prompt expected; use `/mcp`) — an interactive step (§6).

### Phase 5 — Watch mode ✅ **Done**
`rtfm watch ./docs` — FileSystemWatcher with debounce, lock retry, delete/rename
handling, and startup reconcile against the manifest (§2.8).
**Done when:** editing, adding, renaming, and deleting a doc are each reflected
in search results within seconds, and a change made while the watcher was off is
caught up on next start.

*Delivered:* `FolderWatcher` in `Rtfm.Core/Watch` (per-path debounce over a
~500ms quiet window via a `ConcurrentDictionary<key, Pending>` drained on a
`PeriodicTimer`; `IOException` lock-retry with backoff; rename decomposed into
delete-old + upsert-new; `Error`-event logging). Startup reconcile diffs the
folder against a persisted **manifest** — `DocumentManifest` (normalized path →
`{LastWriteUtcTicks, Length}`) + `ManifestStore` (per-`(folder, project)` JSON
under `LocalApplicationData/rtfm/manifests`, atomic temp-file write) — re-indexing
changed/new files, removing vanished ones, and skipping unchanged. The
convert→chunk→index path was extracted into a shared `DocumentIngestor` (also
owns `SupportedExtensions` + file enumeration) so `rtfm index` and `rtfm watch`
agree; `DocumentIndexer` gained `RemoveDocumentAsync`; `rtfm index` now also
writes the manifest so watch starts from a correct baseline. CLI: `rtfm watch
<folder> [--project]` (Ctrl+C → clean shutdown + final flush; all output on
stderr). Verified live against OpenSearch on a throwaway corpus: add / modify /
rename / delete each reflected within seconds, and a stop → edit-while-off →
restart run caught up correctly (re-indexed the changed doc, added the new one,
removed the deleted one, manifest persisted across restarts). The key uses
normalized (lower-cased) paths for coalescing + the index key, but the original
path for file I/O (a lower-cased key won't open on a case-sensitive OS).
**Known limitation:** deleting a whole subdirectory may not fire per-file
`Deleted` events on every OS — the next startup reconcile catches it. `rtfm
index` still does not prune docs deleted since the last run (unchanged from
Phase 3); watch's reconcile is what prunes.

### Phase 6 — Semantic tier ✅ **Done**
Local in-process embeddings, backfill `knn_vector`, hybrid BM25 + kNN with RRF /
normalization via search pipeline. Validate the *conceptual* question half.
**Done when:** "what does Bundle mean" returns the defining passage even when the
query shares few exact words with the doc — without regressing Tier 1 technical
lookups.

*Delivered:* `Rtfm.Core/Embeddings` — `ITextEmbedder`, `LocalEmbedder`
(**all-MiniLM-L6-v2** via ONNX Runtime CPU + `Microsoft.ML.Tokenizers`
`BertTokenizer`; mean-pool + L2-normalize, so the mapping's `l2` space ranks
like cosine), and `EmbeddingModelStore` (auto-downloads model.onnx + vocab.txt
from HuggingFace once, cached under `LocalApplicationData/rtfm/models`;
`RTFM_MODEL_DIR` overrides for offline pre-provisioning). Ingest embeds each
chunk's `ContentWithBreadcrumb` into `content_vector` — the Phase 3 mapping was
already correct (384-dim, hnsw, lucene engine), so this was a true backfill:
re-running `rtfm index` populates vectors, no mapping change. Retrieval:
**OpenSearch 2.17 has no RRF processor (that shipped in 2.19)**, so the §2.10
"confirm wiring" question resolved to the score-normalization route — a
`rtfm-hybrid` search pipeline (`normalization-processor`, min_max +
equal-weight arithmetic_mean) fusing a `hybrid` query's BM25 clause with a kNN
clause (k = clamp(5·topK, 25, 100); the project filter rides on *both* clauses —
`bool` filter lexically, the knn `filter` param vectorside). The client's typed
search has no `search_pipeline` param — passed via the request query string.
**Degradation:** no model (e.g. offline first run) → CLI warns and runs
lexical-only; `DocumentSearch` also falls back to Tier 1 at query time if
embedding fails, so the MCP server keeps serving. Verified live on the real
corpus: the conceptual probe ("who decides what data a user is allowed to see")
was BM25-ranked #2 behind an irrelevant Location chunk; hybrid puts the ABAC
Objective chunk **#1 at 0.98 vs 0.50**. Technical lookups unregressed ("roles
mapped to functions" and exact-token `BUSINESS_LINE__C` keep their top hits);
project scoping verified through the hybrid path; MCP re-verified over raw
stdio (pure JSON-RPC stdout, hybrid ranking, ~0.5s tool call, embedder lazy so
the handshake stays instant).

---

Phases 0–6 delivered the original scope: the tool answers both question types
end-to-end. The phases below extend it — same rules: independently verifiable,
one phase at a time, resist pulling work forward.

### Phase 7 — Pleasant CLI (Spectre.Console) ✅ **Done**
The CLI is a human tool (index/watch/status are dev-operated even though
retrieval UX belongs to the LLM client, §2.11) — make it feel like one.
**Spectre.Console** (pinned at build time) in `Rtfm.Cli` **only** — `Rtfm.Mcp`
stays plain forever (§2.2: stdout is sacred; no UI library goes anywhere near
it).
- `rtfm` — figlet banner + command/env tables instead of the plain usage dump.
- `rtfm ping` — spinner while probing; color-coded cluster status panel.
- `rtfm index` — progress bar over files, ✓/✗ per doc, summary panel.
- `rtfm search` — ranked result cards: score bar, colored breadcrumb, dimmed
  source/date, snippet.
- `rtfm watch` — the showpiece: a live dashboard (folder, project, uptime,
  event counters, scrolling event feed) while watching.
- **Degradation rule:** when stdout/stderr is redirected (non-interactive),
  output stays plain, parseable text — existing stream conventions (results on
  stdout, diagnostics on stderr) and exit codes unchanged. `FolderWatcher`'s
  log callback becomes a small structured event (kind/path/count) whose
  `ToString()` is today's exact line format, so scripts keep parsing.
**Done when:** each command renders the rich UI in an interactive terminal; the
watch smoke scripts still pass unmodified against redirected output; `dotnet
test` green; MCP raw-stdio check still shows pure JSON-RPC.

*Delivered:* `Spectre.Console` 0.57.1 in `Rtfm.Cli` only. `Ui` holds two
consoles honoring the stream conventions — `Ui.Out` (stdout: results, help,
ping report) and `Ui.Err` (stderr: index progress/summary, watch dashboard) —
with `Ui.Fancy` gating live rendering and all dynamic text markup-escaped.
Banner + command/env tables; ping spinner + color-coded panel; index progress
bar (embedder warms up *before* the live render so download logs don't
interleave) + ✓/✗ summary table; search result cards with 10-cell score bars;
watch live dashboard (status dot, uptime ticker, indexed/removed/failed
counters, last-12 event feed) via `Live` on stderr. `FolderWatcher`'s callback
became `Action<WatchEvent>` (kind/path/chunks/detail); `WatchEvent.ToString()`
reproduces the pre-Phase-7 lines byte-for-byte and unit tests pin that
contract, so the redirected fallback (and the smoke scripts parsing it) is
unchanged. Verified: 51/51 tests, watch smoke script green unmodified, MCP raw
stdio still pure JSON-RPC, index/search/ping exercised against the live stack.

### Phase 8 — MCP tool surface v2: `get_document`, `list_sources`, `find_similar` ✅ **Done**
The §2.11 "possible later additions", promoted: using the tool in anger shows
the agent often needs more than a 1600-char chunk.
- `get_document(path, project?)` — the full converted markdown of one document,
  reassembled from its chunks in ordinal order (the source file may be MHTML —
  the *converted* form is what the agent can read). For when a hit is right but
  the answer sprawls past chunk boundaries.
- `list_sources(project?)` — enumerate indexed docs (path, title, project,
  `source_modified_at`, chunk count). Gives the agent *corpus awareness*: it can
  tell "the docs don't cover this" apart from "my query was bad", and can cite
  what exists before drilling in.
- `find_similar(path, top_k?)` — semantically nearest *other* documents, via
  mean of the doc's chunk vectors (or its top chunk) against `content_vector`.
  Cheap now that Tier 2 exists; useful for "what else discusses this?".
All three are aggregations/lookups over fields already indexed — no mapping or
ingest changes.
**Done when:** from Claude Code, an answer that spans chunk boundaries can be
completed via `get_document`; `list_sources` returns the full corpus with
correct metadata; `find_similar` on the RBAC doc surfaces the ABAC doc.

*Delivered:* `DocumentCatalog` in Core (`CatalogModels` records) +
`CatalogTools` in Mcp; `DocumentCatalog` registered in DI. `list_sources` =
`terms` agg on `source_path` (+ `top_hits` for title/project/date, `doc_count`
as chunk count). `get_document` = chunks fetched ordinal-sorted and reassembled
(title once, each breadcrumb once — skipped when it repeats the title —
`ContentWithBreadcrumb` prefix stripped); reassembly from *chunks*, not
re-reading the file, because the stored path key is lower-cased and won't open
on case-sensitive filesystems (§2.12); overlap seams may repeat a few lines —
documented in the tool description. `find_similar` = mean of the doc's chunk
vectors, L2-normalized, kNN with `must_not` self + project filter, best chunk
per candidate doc wins; explicit `vectorsAvailable=false` note when the doc was
indexed lexical-only. Path arguments resolve exact-first then `*/filename`
wildcard, so the short `source` names from `search_docs` work; `search_docs`
hits now also carry the full `path` for chaining (its description points at the
new tools). Verified over raw stdio: four tools advertised; `list_sources`
returns the 5-doc corpus with correct counts; `get_document` by bare filename
reassembles 25 chunks (~22 K chars, title first); **`find_similar` on the RBAC
doc ranks the ABAC doc #1 (0.60)** — the acceptance criterion. 59/59 tests.
In-Claude-Code confirmation needs the usual restart to reload the server.

### Phase 9 — Format expansion: PDF, Excel, CSV ✅ **Done**
Widen §2.5's converter fan-in; the shared tail (strip → markdown → chunk) stays
untouched, so each format is only a new front end + detection rule.
- **PDF** — candidate: `UglyToad.PdfPig` (pure managed, MIT). Detect by `%PDF`
  magic. Text extraction with heading *heuristics* (font size/weight) — PDFs
  carry no real heading semantics, so expect flatter breadcrumbs; page-window
  chunking is the fallback. PDF tables are the known hard part — start with
  text-run extraction and accept imperfect tables rather than pre-building a
  table reconstructor.
- **Excel (`.xlsx`)** — candidate: `ClosedXML`. Detect: zip magic like docx, so
  disambiguate by container content (`xl/` vs `word/`). Each sheet becomes a
  section (breadcrumb: `Workbook > Sheet`), its used range a pipe table. The
  chunker already splits oversized tables by rows repeating the header row —
  built for exactly this.
- **CSV** — near-trivial: one pipe table, filename as title; extension-detected.
  No new dependency unless quoting/edge cases demand one.
Pin exact package versions at build time, per §3.
**Done when:** a representative PDF, a multi-sheet workbook, and a CSV each
index and answer a lookup question about their own content; existing formats
unregressed.

*Delivered:* `PdfConverter` (PdfPig — **NuGet ID is `PdfPig`,** the
`UglyToad.PdfPig` ID is stale/unlisted), `XlsxConverter` (ClosedXML),
`CsvConverter` (hand-rolled RFC 4180-ish parser + comma/semicolon/tab sniff, no
dependency). `FormatDetector` gained `%PDF` magic, `.csv` extension, and the
zip disambiguation (`xl/` vs `word/` container folders; unreadable/markerless
zips default to docx, the historic behavior). PDF headings are heuristic as
planned: ½pt-bucketed letter sizes, word-count-weighted median = body, short
larger (or bold-at-body) lines become headings, distinct sizes → #/##/###;
paragraphs split on >1.6× typical baseline gap; embedded `D:yyyymmdd…` dates
parsed for `source_modified_at` (xlsx uses workbook `Properties.Modified`; CSV
has none → mtime). Sheets render as `# workbook` + `## sheet` + pipe table, so
breadcrumbs come out `workbook > sheet`. **Rename:** the `DocumentFormat` enum
became `SourceFormat` — ClosedXML drags in the `DocumentFormat.OpenXml`
package, whose root namespace shadows the enum everywhere (and it's also our
documented docx-tables fallback dep, so the collision was permanent). Unit
tests build fixtures in-memory (PdfPig's `PdfDocumentBuilder`, ClosedXML
workbooks). Verified live: handbook.pdf / infrastructure.xlsx / oncall.csv
indexed under a smoke project — "how many vacation days" → `Team Handbook >
Vacation Policy` (PDF, 1.00), "gamma-cache port" → `infrastructure > Servers`
(xlsx), "who is on call in week 2026-W28" → the roster row (CSV); pam corpus
re-indexed to the same 111 chunks with its top hits intact. 81/81 tests.

### Phase 10 — Observability: `rtfm status` + staleness ✅ **Done**
The index is a black box unless you curl OpenSearch. One read-only command:
- `rtfm status` — per project: doc/chunk counts, newest/oldest
  `source_modified_at`, last `indexed_at`, vector coverage (% chunks embedded);
  plus environment: OpenSearch reachable, model cached, manifest freshness.
- `rtfm status --stale <days>` — list docs whose `source_modified_at` is older
  than the window. The corpus is *manual exports* (Confluence pull is a
  non-goal, below) — drift from the live wiki is invisible, so surface age
  instead. Also fold a one-line hint into the `search_docs` description so the
  agent treats very old `source_modified_at` with suspicion (§2.13 B already
  carries the date).
**Done when:** `rtfm status` reports accurate counts/dates against the live
index, and `--stale` flags a deliberately-backdated doc.

*Delivered:* `StatusService` in Core — one size-0 aggregation (terms on
`project`; per bucket: `cardinality(source_path)`, chunk `doc_count`, min/max
`source_modified_at`, max `indexed_at`, and an `exists(content_vector)` filter
sub-agg for vector coverage) — plus `ManifestStore.ListAll()` (project, folder,
tracked files, last-saved). `rtfm status [--project] [--stale <days>]` renders
an environment panel (cluster health, model cache, manifest count), a
per-project table (docs/chunks/vectors %/source span/last indexed), the
manifests table, and — with `--stale` — documents older than the window
(client-side filter over `DocumentCatalog.ListSourcesAsync`; no new query
machinery). `search_docs`'s description gained the drift hint: treat very old
`source_modified_at` with suspicion, mention the date. Verified live: pam shows
5 docs / 111 chunks / 100% vectors / correct span; a doc backdated to
2024-01-15 was flagged by `--stale 365` at 899d. **Bonus catch:** the first
live run exposed 21 leaked watch manifests from unit tests and pre-`purge`
smoke runs — `ManifestStoreTests` now uses unique `test-…` project names and
purges in `finally`, and the litter was swept. 84/84 tests.

### Phase 11 — Tier 3 retrieval: cross-encoder reranking ✅ **Done**
The next precision lever after hybrid (§2.10): retrieve generously (the hybrid
k is already 25+), then rerank the candidates with a small local cross-encoder
(candidate: ms-marco-MiniLM family via ONNX — same runtime, tokenizer, and
model-store machinery as Phase 6) and return the top `top_k`. Query-time only —
no ingest or mapping changes; reuse `EmbeddingModelStore` for the second model.
Same degradation rule as Tier 2: no model → skip reranking, loudly.
**Done when:** a query set over the real corpus shows reranked top-3 ≥ hybrid
top-3 (spot-check, not benchmark), with no query slower than ~1s end-to-end.

*Delivered:* `CrossEncoder` (+ `IReranker`) in `Rtfm.Core/Embeddings` —
**ms-marco-MiniLM-L-6-v2** via the same ONNX Runtime + `BertTokenizer` +
model-store machinery as Tier 2 (`EmbeddingModelStore` generalized with a
`ForReranker` factory; per-model cache subfolders; note the tokenizer property
is `SeparatorTokenId`, not `SepTokenId`). Pair encoding is `[CLS] q [SEP] p
[SEP]` with segment ids, built by concatenating two singly-encoded sequences
and truncating the passage side. `DocumentSearch` over-fetches
(clamp(3·topK, 12, 20)) when a reranker is present, reorders by cross-encoder
score (sigmoid-squashed for display), and keeps the fused order — loudly — if
the model is unavailable. **The engineering finding worth remembering:** naive
full-chunk scoring *regressed* quality — MS MARCO cross-encoders are trained on
~60-token passages, and 1600-char chunks diluted every score to logit −5..−11
(the conceptual probe's right answer scored −1.3 as a snippet but −5.5 inside
its chunk, and noise won). Fix: **max-window scoring** — each candidate splits
into ~1000-char windows with 200 overlap, every window re-prefixed with its
breadcrumb, and a hit's best window speaks for it. Validated on a five-query
spot-check vs Tier 2: reranked top-3 ≥ hybrid on all five, strictly better on
two (off-topic chunks evicted from top-3), with sharper confidence separation;
steady-state MCP latency 150–677 ms/query. `rtfm init --with-model` now
prefetches both models (~90 MB each). 93/93 tests.

### Phase 12 — Proactive contradiction detection (§2.13's Tier-2 add-on) ✅ **Done**
The identity feature: docs rot and disagree, and nobody notices until it burns
someone. At ingest, compare each new chunk's vector against *similar* chunks
from **other documents in the same project** (cross-project differences are
expected — §2.14). High semantic similarity + disagreeing content ⇒ record a
candidate pair (source paths, ordinals, similarity, timestamps) into a small
side index (it must survive re-index of either doc; delete pairs when either
side's doc is re-ingested and re-evaluate).
- Surfacing: `rtfm contradictions [--project]` lists pairs newest-first; a
  `list_contradictions` MCP tool exposes the same so the agent can warn.
- Judging "disagreement" needs care: similarity alone flags near-duplicates.
  Start dumb (similar + different dates + not near-identical text) and let the
  LLM do the actual contradiction reasoning at read time (§2.13 B) — RTFM only
  *nominates* pairs. No auto-resolution.
**Done when:** planting a doc that contradicts an existing statement (the §2.13
`admin` vs `super-admin` example) yields exactly that pair in
`rtfm contradictions`, and the MCP tool returns it with both dates.

*Delivered:* `Rtfm.Core/Contradictions` — `ContradictionIndex`
(`rtfm-contradictions` side index: pair fields, unindexed excerpts),
`ContradictionPair` (deterministic id = hash of the sorted chunk keys, so
re-detection upserts; side A = newer doc), and `ContradictionDetector`. At
ingest (`DocumentIngestor`, batch *and* watch): refresh main index → per-chunk
kNN vs other docs in the same project (k=3, must_not self, score floor 0.75 ≈
cosine 0.83) → the dumb §2.13 filter (different `source_modified_at`
timestamps + texts differ after case/whitespace normalization — identical text
is a copy, not a contradiction) → best candidate per chunk nominated. Pairs
referencing a doc are dropped whenever it re-ingests or is removed, then
re-evaluated — the side index survives §2.9's delete-and-reindex without going
stale. Surfacing: `rtfm contradictions [--project]` (newest first, both sides
with file/date/heading/excerpt) and the `list_contradictions` MCP tool, whose
description carries the §2.13 B protocol (read both via `get_document`, prefer
newer, surface the conflict — never silently choose). `rtfm purge` also drops
the project's pairs. Verified live: the planted `admin` vs `super-admin` pair
is the *only* nomination (sim 0.83, both dates correct in CLI and MCP);
re-index leaves exactly 1 pair (idempotent). Real-corpus noise gauge: 2
nominations across pam's 111 chunks, both Confluence *template boilerplate*
(shared "Document Owners"/"PRD Approval" tables) — the anticipated noise mode,
absorbed by LLM-judges-at-read-time. 102/102 tests.

### Phase 13 — Corrections that survive re-index (§2.13 C, decision + build) ✅ **Done**
When an agent + user confirm "the docs are wrong / outdated here", persist that
knowledge. Leading option per §2.13 C: a separate **overrides index** (option 2)
— keeps `rtfm-docs` purely derived (§2.9 delete-by-query stays safe), merged at
query time; an override records scope (project + source path or topic), the
correction text, author, and timestamp. MCP tools: `add_note` /
`list_notes` (naming TBD), human-in-the-loop only — the agent proposes, the
user confirms. **This phase starts with a design pass on §2.13 C's options
before any code.**
**Done when:** a confirmed correction outranks/annotates the stale passage in
`search_docs` results, survives a full re-index of the corpus, and is visibly
attributed as an override (never silently masquerading as source text).

*Delivered:* design pass resolved §2.13 C to **option 2** (see the updated
§2.13 C for the reasoning — the killer argument against source write-back:
exports get clobbered by the next export). `Rtfm.Core/Notes` — `NotesIndex`
(`rtfm-notes`: `rtfm_technical` analyzer + 384-dim vector, mirroring the main
index so notes match lexically and semantically), `Note`, `NotesStore`
(add/list/remove/purge/search/find-anchored; note text embedded at add time,
lexical-only fallback). Retrieval merge in `DocumentSearch`: matching notes
(kNN floor **0.6** — deliberately looser than contradiction detection's 0.75
because query→statement similarity runs structurally lower than
statement→statement; a relevant note scored 0.67 in live validation) join the
rerank pool as `origin:"note"` hits; without a reranker notes lead (no shared
score scale across indexes); anchored notes attach to doc hits as
`annotations`. All note failures degrade loudly, never failing the search.
Surface: `rtfm note add|list|rm` (typing the command is the confirmation),
`add_note`/`list_notes`/`remove_note` MCP tools (descriptions enforce the
human-in-the-loop precondition: propose → explicit user yes → call), `rtfm
purge` drops project notes, `search_docs` hits carry
`origin`/`author`/`annotations` and its description teaches override
semantics. Acceptance verified live: the confirmed correction ranks **#1 as ⚠
OVERRIDE NOTE (attributed)** with the stale passage #2 carrying the
annotation; a full corpus re-index changes nothing; purge removes the note.
8 MCP tools advertised. 108/108 tests.

### Phase 14 — Packaging & distribution
Adoption currently requires cloning the repo and building. Ship `rtfm` +
`rtfm-mcp` as proper artifacts: `dotnet tool install -g` (or single-file
published exes), with `.mcp.json` guidance updated to match (§6's "published
single-file exe is the cleaner long-term answer"). Include the §2.12 setup
notes (`vm.max_map_count` on native Linux) in the install story. Can be pulled
earlier if a second user shows up before Phases 8–13 land.
**Done when:** a machine without the repo can install both tools, run
`docker compose up -d` + `rtfm index`, and wire the MCP server into Claude Code
from the published artifact alone.

### Phase 15 — draw.io diagrams ✅ **Done** *(built out of order — Phase 14 still open)*
Diagrams carry knowledge (DB table relations, service topologies) that is
invisible to text search — but draw.io files are XML *graphs*, not pictures,
so the knowledge is extractable.
**Done when:** an ER-style `.drawio` schema indexes and answers "which tables
reference X" with the right page section.

*Delivered:* `DrawioConverter` — a new §2.5 front end, no tail changes. Parses
the `mxfile` per page (`<diagram>`), handling **both** page encodings: modern
plain `mxGraphModel` children and the classic compressed payload (base64 → raw
DEFLATE via `DeflateStream` → URI-decode). Each page renders as `## <page>`
with **Shapes** (top-level vertices; containers inline their descendant labels,
so an ER table reads `**accounts** — PK account_id; tenant_name`, rows merging
their marker+name cells) and **Connections** (`A → B: label`, edge endpoints
resolved by walking up to the nearest labeled ancestor — covers edges attached
to table *rows*). Labels are HTML fragments → stripped via AngleSharp; `mxfile
modified` attr → `source_modified_at`. Detection: `<mxfile` content sniff
(wins for `.xml` files too) + `.drawio` extension. Verified live: a two-page
schema/deployment diagram indexed as 4 breadcrumbed chunks; "which tables
reference the accounts table" hits the Schema>Shapes chunk **#1**. Caveat
noted: the cross-encoder's *absolute* scores run low on symbol-dense diagram
notation, but relative ranking holds. Fixture tests cover the compressed
round-trip (encoded exactly as draw.io does). **Out of scope for now:**
`.drawio.png`/`.drawio.svg` (XML embedded in image containers) — add if the
real corpus contains them.

**Real-corpus finding (same day):** production diagrams (Mermaid-imported ER
models) wrap shapes in `<UserObject label=… id=…><mxCell …/></UserObject>` —
the *wrapper* owns the id and label, the inner `mxCell` owns neither, so every
entity converted nameless. Fix: mxCells inherit missing id/label from a
`UserObject`/`object` wrapper parent. After the fix the two real diagrams
extract fully (24-entity identity/tenancy data model with names, columns, PK/FK
markers, and 38 labeled relations like `PartyOrganisation → Tenant: "is
tenant"`). Validated: "which tables reference the Tenant table" answers from
the diagram at #1. Known limit: an entity buried in a large all-entities
Shapes chunk can lose to prose docs on vague queries (the symbol-density
caveat in practice) — revisit per-entity chunk granularity if it bites.
118/118 tests.

### Phase 16 — OCR for images embedded in PDFs ✅ **Done**
Diagrams saved as *pictures* inside PDFs carry knowledge no text extractor can
reach. Two cases: vector-text diagrams (PdfPig already extracts those words —
solved since Phase 9) and raster images, which need OCR.
**Done when:** a PDF whose knowledge exists only inside an embedded raster
image indexes and answers a question about that content.

*Delivered:* **RapidOcrNet 2.0.0** (Apache-2.0, by a PdfPig maintainer) —
PaddleOCR PP-OCRv5 models via the same `Microsoft.ML.OnnxRuntime` we already
ship; the models (~13 MB det/cls/rec + dict) live *inside* the NuGet package,
so there is no download and no offline degradation path. `PdfConverter` now
extracts each page's embedded images (PdfPig `GetImages()`; PNG re-encode with
raw-bytes/JPEG fallback via SkiaSharp), skips icons (shorter side < 80 px),
caps at 8 images/page, OCRs the rest, and appends each result as an
`[Image text] …` paragraph at that page's end. One shared lazy engine per
process, initialized with **explicit absolute model paths** — the package
default resolves against the CWD, which for a CLI is wherever the user stands.
**Packaging gotcha:** RapidOcrNet's `.targets` copies models only for *direct*
references (`build/`, no `buildTransitive/`) — Core carries explicit
`None`+`CopyToOutputDirectory` items so `models\v5\` flows to the Cli/Mcp/test
outputs (version-locked to the pin; bump together). Any OCR failure skips the
image, never the document. Verified: unit fixtures (SkiaSharp-drawn diagram
JPEG embedded via PdfPig's writer → labels extracted; tiny icon skipped) and
live end-to-end — a PDF whose retention policy existed only as pixels answers
"how are alerts escalated and what is the retention" at **#1 (1.00)**.
Notably, OCR'd prose scores *well* on the cross-encoder (unlike drawio symbol
notation). 120/120 tests.

**Deliberately not planned:** Confluence API pull (auth/token/rate-limit sprawl;
manual exports remain the ingestion contract for now — Phase 10's staleness
surfacing is the mitigation), web UI (the LLM client is the UX, §2.11), cloud
sync/hosting (per-dev local is the model, §intro).

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
      "args": ["${RTFM_HOME:-.}/src/Rtfm.Mcp/bin/Release/net10.0/rtfm-mcp.dll"],
      "env": {
        "RTFM_OPENSEARCH_URL": "http://localhost:9200",
        "RTFM_PROJECT": "pam"
      }
    }
  }
}
```

**This same template serves every consuming repo** (the multi-repo workflow —
see the README's "Using RTFM from your other repos"): Claude Code expands
`${RTFM_HOME:-.}` at launch, so inside the rtfm repo the fallback `.` keeps the
path relative to the repo root, while other repos resolve it via the per-dev
`RTFM_HOME` env var pointing at the rtfm clone. Only `RTFM_PROJECT` differs per
repo (§2.14 auto-scoping). Each Claude Code instance spawns its own `rtfm-mcp`
process against the shared OpenSearch — stateless readers, no coordination
needed; each loads its own embedding model (~100–200 MB) on first search.
Phase 14 (packaging) collapses the template to a bare `rtfm-mcp` command.

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
- **Build gotcha — refresh the CLI before running it by hand.** `dotnet test`
  (and `dotnet build` of just `Rtfm.Core`) rebuilds Core but **not** the
  `Rtfm.Cli` / `Rtfm.Mcp` executables — they aren't in the test build's graph.
  Their `bin/` keeps the *previously copied* `Rtfm.Core.dll`, so invoking the
  built DLL directly (`dotnet src/Rtfm.Cli/bin/.../rtfm.dll …`) silently runs
  **stale** Core code after a Core-only change. Fix: build the whole solution
  (`dotnet build Rtfm.slnx -c Release`) before invoking a built DLL, or just use
  `dotnet run --project src/Rtfm.Cli -- …`, which rebuilds the exe and its
  references first. Same trap applies to the MCP server: rebuild before you
  reconnect (§2.2, §6).
- Don't build Tier 2 (embeddings/hybrid) or the Open XML table fallback until
  their triggering conditions are actually hit. Resist scope creep — the joke is
  that the answer was in the docs all along, not that we boiled the ocean.
- Prefer raw OpenSearch query bodies where the typed client is constraining.
- **Commits: no self-attribution.** Do not add `Co-Authored-By: Claude`
  trailers, "Generated with Claude Code" lines, or any similar attribution to
  commit messages, PR descriptions, or code comments. Write commit messages as
  the developer, nothing more.
- Domain note: the real corpus (`docs/`) is product/architecture material for an
  access-control / multi-tenant platform — RBAC product & role classification,
  ABAC segmentation, an MSP central-configuration model, a workflow engine PRD,
  and location confidence/impact. It mixes exact technical terms (roles,
  attributes, config keys) with conceptual definitions — exactly the mixed
  lexical/conceptual profile this design targets.
