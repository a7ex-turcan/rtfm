# RTFM

> **R**etrieval **T**ool **F**or **M**anuals.
> (The team knows what it really stands for. The README does not.)

A local, per-developer documentation retrieval tool. It indexes a folder of
documentation — Confluence MHTML exports (`.doc`), Word `.docx`, plain
Markdown (`.md`), PDF, Excel (`.xlsx`), CSV, draw.io diagrams, and exported
email chains — into a
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
        │   tool: query_database(db, sql)    → rows  ──────────────┼──┐
        └───────────────────────────┬─────────────────────────────┘  │
                                     │  stdio (MCP)                   │ SQL (read-only,
                                     ▼                                │  live — §2.15)
        ┌─────────────────────────────────────────────────────────┐  │
        │  LLM client  (Claude Code / Claude Desktop / IDE)         │  │
        └─────────────────────────────────────────────────────────┘  │
                                                                      ▼
                                              ┌──────────────────────────────┐
                                              │  your database (.rtfmdb)      │
                                              │  schema in, data out          │
                                              └──────────────────────────────┘
```

The last arrow is the odd one out: every other box reads the *derived*
OpenSearch index, but `query_database` reads live data straight from your
database (§2.15). Everything else here is disposable and rebuildable; that
isn't.

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

> **The `.doc` extension lies two different ways.** Confluence's "Export to
> Word" is MHTML (above); **Jira's "Export to Word" is *bare* HTML** — a plain
> `<!DOCTYPE html>` document (with an `application/vnd.ms-word` meta tag) and
> **no** MIME wrapper. Same `.doc` extension, different container, so they route
> to different front ends. The bare-HTML route (`HtmlConverter`) reuses the
> shared strip→ReverseMarkdown tail; it also recovers the two things the tail
> can't see once `<head>` is stripped — the `<title>` (Jira issues carry no
> `<h1>`) and the Jira `Updated:` byline, used as `source_modified_at` (§2.13 A,
> the "Confluence last modified byline" analog). `.html`/`.htm` files ride the
> same route.

**Route by sniffing content, not just the extension** — the sample files prove
the extension lies (a `.doc` that is really MHTML, or really bare HTML).
Detection order (content first; MHTML *before* HTML, since MHTML also wraps
HTML):
- zip magic `PK\x03\x04` → **docx**
- MIME headers **plus conversational headers** (`From:`/`To:`/`Subject:`/`Date:`)
  → **email** (Phase 24 — must be tested *before* the MHTML rule, which a `.eml`
  file would otherwise match; see that phase's note)
- MIME headers (`MIME-Version:` / `multipart/related`) → **MHTML**
- `<!doctype html>` / `<html>` (or `.html`/`.htm` extension) → **bare HTML**
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

The tool surface is now fifteen tools: `search_docs` (hits carry chunk
`ordinal` since Phase 21); the Phase 8 additions —
`get_document(path, project?, around_ordinal?, radius?)` (full reassembled
page, or just the section around a hit), `list_sources(project?, full?)`
(corpus awareness; unscoped calls summarize per project),
`find_similar(path, top_k?, project?)` (related docs via chunk-vector
centroid); `list_contradictions(project?, top_k?, include_closed?)` (Phase 12
nominations, Phase 22 kind/status); the Phase 13 correction tools —
`add_note` (human-confirmed only; retry-safe deterministic ids), `list_notes`,
`remove_note`; `save_document` (Phase 19 write-back); the Phase 21 additions —
`list_projects` (project discovery) and `ping` (fast liveness probe); and the
Phase 22 lifecycle verdicts — `dismiss_contradiction` and
`resolve_contradiction` (both human-confirmed only; resolve records the
correction as an override note anchored to the older side); and the Phase 23
live-data gateway — `list_databases` and `query_database` (read-only, opt-in
per descriptor, row-capped; §2.15).

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
- **OCR bitmap color type is platform-dependent.** `SKBitmap.Decode` returns
  the platform-native color type — `Bgra8888` on Windows/Linux but `Rgba8888`
  on macOS — and RapidOcrNet's mean-normalize only accepts `Bgra8888`/`Gray8`,
  so on macOS every image threw `ArgumentException`. `OcrEngine.DetectText`
  normalizes to `Bgra8888` before OCR; keep any new OCR entry point on that
  path. (Caught by the CI macOS leg, invisible on the author's Windows box —
  the reason the §5 test matrix spans all three OSes.)

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

### 2.15 Live data plane (`query_database`) — the fourth arrow
Phase 20 made a database's *schema* indexable. Phase 23 lets an agent read the
database's *data*. This is a genuine change to what RTFM is, so it is recorded
as a decision rather than treated as one more tool:

- **Everything else in RTFM is a derived, disposable index.** `rtfm-docs` can be
  dropped and rebuilt from the sources; §2.9's delete-by-query is safe precisely
  because nothing there is authoritative. `query_database` is different: it
  bypasses OpenSearch entirely and reads the live database
  (`rtfm-mcp` → your DB — the fourth arrow on §1's diagram). The §1 picture is
  no longer the whole truth, and neither is "Retrieval Tool For Manuals".
- **Why it belongs here anyway:** an agent that can query a DB but doesn't know
  the schema writes garbage SQL. RTFM already indexes the schema (Phase 18/20),
  so the pairing — search the schema, write correct SQL, run it — is what makes
  the query tool *good* rather than a footgun. The tool descriptions push that
  schema-first workflow explicitly.
- **Opt-in, per descriptor (load-bearing).** A `.rtfmdb` is queryable only if it
  carries a `query` block. Descriptors written before Phase 23 meant "pull my
  schema" — shipping the query tool must not silently turn them into live query
  endpoints. Absent block ⇒ refused, and `list_databases` says "schema only".
- **Separate credential.** Schema pull needs catalog read; querying needs table
  read. The `query` block takes its own `connectionString` (`${ENV}` expanded)
  so the query path isn't handed whatever read/write user the schema pull uses.
  Omitting it falls back to the schema-pull string — still gated by the
  read-only enforcement below, never by trust.
  → **Env expansion is therefore lazy** (`ResolveConnectionString` /
  `ResolveQuery`), not done at parse time: the CLI (indexing) and the MCP server
  (querying) are different processes holding different secrets, so parsing a
  descriptor must never require a var the current process doesn't have.
- **Reads by default; writes are a second opt-in** (`"allowWrites": true` on the
  query block). Writes are genuinely useful against a local test DB ("seed some
  rows"), so they're supported — but a database is read-only until its
  descriptor says otherwise, because the common case is an agent exploring data
  it should not mutate.
- **What the read guard is, and is not.** It is a guard against an agent's stray
  write, **not a security boundary** — RTFM is a per-dev local tool (§intro) and
  the descriptor points wherever you point it. Anyone aiming this at production
  owns that choice. It *is* enforced at the database, and the mechanism differs
  by provider because their capabilities do:
  - **Postgres** — `SET TRANSACTION READ ONLY`. A write raises `25006` in the
    engine, so the agent gets an immediate, legible error.
  - **SQL Server** — has **no** transaction-level read-only mode (and
    `ApplicationIntent=ReadOnly` is *not* a security control — it's an
    availability-group routing hint). But SQL Server's DML **and DDL** are both
    transactional, so read mode runs the statement in a transaction that is
    **always rolled back**. Same practical guarantee, no refusal.
  - **Not a login-permission check.** An earlier cut probed the login and
    refused anything that could write. That is unusable for the actual audience:
    a local dev box connects as `sa`/`db_owner`, so it would have refused *every*
    query on the provider it was supposed to protect. The transaction guard holds
    even on a superuser connection — verified.
  - **A rolled-back write is reported as an error, never as a silent success.**
    "ok, 0 rows" after an undone INSERT would leave the agent believing it wrote.
  - **Filtering the SQL string for `DROP`/`DELETE` is theater** — CTEs,
    `SELECT INTO`, side-effecting functions, and stacked statements all walk
    past it. Deliberately not attempted; it would be false assurance.
  - **Known limit:** a rollback cannot undo what escapes the transaction — a
    procedure doing its own COMMIT, identity/sequence consumption, `xp_cmdshell`.
- **Egress is capped, and truncation is *detected*, not assumed.** Default 500
  rows (per-descriptor `maxRows`, hard cap 5000); the reader fetches one row
  past the cap so `truncated: true` is a fact, letting the agent narrow its query
  instead of believing it saw the whole table.
- **Discovery reuses the watch manifests, not a new env var or the index.** The
  stored `source_path` key is lower-cased and won't reopen on a case-sensitive
  filesystem (§2.12) — the Phase 8 trap — so descriptors are found by scanning
  each manifest's recorded folder (original casing) for `*.rtfmdb`. The
  precondition (the folder was indexed) is already met by having the schema
  searchable at all.

### 2.16 Live issue-tracker pull (Jira) — reversing the "no API pull" stance
Every ingestion route so far is a *file* a human dropped in a folder. Phase 25
adds the first source RTFM reaches out and pulls over an authenticated API: a
Jira ticket, on demand (`rtfm jira index AEXP-123`). Phase 24 explicitly
deferred "Confluence API pull (auth/token/rate-limit sprawl)"; this is the same
class of thing, so reversing that for Jira is a decision, not a drive-by.

- **Why reverse it now.** The pain the deferral accepted — manually exporting
  and re-exporting docs — is *worst* for tickets: they change constantly, link
  to each other, and the decisions in their comment threads (the `admin` →
  `super-admin` kind) never make it back to Confluence. And a ticket carries
  what §2.13 recency has been starving for: a **real per-item `updated` date**
  and **real authors**, so supersession reasoning finally has trustworthy
  input (the file corpus shares mtimes — Phase 22 found nothing real to label).
- **It reads, only ever.** `JiraClient` issues `GET` and nothing else — there
  is no create/update/delete method on it, by construction. This is the §2.15
  read-guard lesson taken to its limit: the safest write guard is having no
  write path at all. RTFM is a retrieval tool; it must never mutate a team's
  tracker. (Load-bearing invariant, not a preference.)
- **Fits the batch model (§2.4), does not become a live plane (§2.15).** Unlike
  `query_database`, Jira tickets still land in the derived, disposable
  `rtfm-docs` index — pull → convert → chunk → embed → index, the normal tail.
  What's new is only the *front* of the pipe (an HTTP pull instead of a file
  read) and that ingest is triggered by ticket key, not folder scan.
- **Secrets: same discipline as `.rtfmdb`.** The stored config
  (`LocalApplicationData/rtfm/jira/<project>.json`, written by `rtfm jira
  config`, never committed) holds the workspace URL and account email (not
  secret) plus a **`${ENV}` reference** to the API token, expanded lazily at
  call time (shared `EnvironmentExpansion`, §2.15's `ResolveConnectionString`
  pattern). The token itself lives only in the environment. Jira Cloud auth is
  HTTP Basic `email:token`.
- **Synthetic source key, deliberately *not* `PathNormalizer`.** A ticket has
  no file path; its `source_path` is `jira://AEXP-123` (canonical upper-cased
  key, built by `JiraSource.Key`). This must **not** flow through
  `PathNormalizer` — it calls `Path.GetFullPath`, which would mangle the URI
  into a filesystem path. `JiraSource.Key` is the Jira analog of the normalizer:
  the one builder used identically on index and delete so exact-match
  delete-by-query (§2.9) holds.
- **Thread granularity, a fourth time (§§2.5, 15, 18, 24).** Ticket = document,
  each comment = its own chunk. Breadcrumb `AEXP-123 <summary> > Comment by
  <author>, <date>`, synthesized from ticket structure exactly as email
  synthesizes it from thread structure — a question about what was decided in
  comment 7 should hit comment 7's chunk. Description and comment bodies come
  back from Jira as **rendered HTML** (`expand=renderedFields`; the raw fields
  are Atlassian Document Format JSON), so they reuse the shared
  strip→ReverseMarkdown tail with no new converter. (Comment *dates* in
  `renderedFields` are display-formatted, so machine dates + authors are read
  from the raw `fields.comment` and joined to rendered bodies by comment id.)
- **Graph traversal is leashed (§Phase 25 step 2).** "Follow every linked
  ticket" will try to eat the instance; three independent caps —
  `maxDepth`, a hard `maxTickets` budget, and depth-degrading fidelity — bound
  every run, a visited-set keys circular refs by ticket, and dropped work is
  logged (no silent caps, §5). Text-mention edges (`AEXP-\d+` in prose) are
  opt-in and only followed from the seed.
- **Watch is a poll loop, not `FileSystemWatcher`.** An API has no filesystem
  events; a separate `rtfm jira watch` re-pulls the monitored set on an
  interval (`key in (…) AND updated >= <since>`). Kept a *separate* command
  from `rtfm watch` on purpose — two different lifecycles (OS events vs.
  polling) should not tangle. A monitored-set registry (manifest-shaped)
  persists the *expanded* key set, and `rtfm jira purge` drops it so monitored
  tickets don't pile up.

### 2.17 Live wiki pull (Confluence) — the Jira model, applied to pages
Phase 26 adds Confluence the same way §2.16 added Jira: an authenticated,
read-only API pull of pages into the derived index, on demand, with deep
traversal and a polling watch. It reuses §2.16's decisions wholesale — the ones
below are only what differs because Confluence is a *wiki of pages*, not a
tracker of tickets.

- **Same read-only, batch-model, `${ENV}`-secret, synthetic-key discipline as
  §2.16.** `ConfluenceClient` is GET-only; the config (`${ENV}` token, default
  `CONFLUENCE_TOKEN` — the same Atlassian account, so either product token
  works) is per-project under `LocalApplicationData/rtfm/confluence`; the source
  key is `confluence://{pageId}` built by `ConfluenceSource.Key` (again *not*
  through `PathNormalizer`). Same workspace URL + email as Jira; kept a separate
  config so the two products stay independent.
- **The seed is a *page*, led by its URL; a whole *space* is the bulk option.**
  Confluence has no single atomic unit like a ticket — knowledge lives in a
  tree of pages inside spaces. So `rtfm confluence index <URL|id>` seeds from
  the page a user copied from their browser (the id is parsed from
  `.../pages/{id}/…`), and `--space <KEY>` enumerates every page in a space
  (budget-capped) as the bulk alternative. Both funnel into the same crawler.
- **Traversal follows child pages + in-body page links, not ancestors.** The
  two edges chosen: the descendant **page tree** (`children.page`) and
  **in-body links** to other pages (`/pages/{id}/` hrefs in the rendered body,
  cross-space included) — the "referenced links". Deliberately *not* ancestors:
  walking upward to parents would pull in unrelated siblings and widen the
  crawl without adding focused value. Same depth + `maxPages` budget leash,
  visited-set for cycles, dropped-count reported.
- **Reuses more of the pipeline than Jira — a page is already a document.**
  `body.view` comes back as rendered HTML with **real headings**, so it feeds
  the shared strip→ReverseMarkdown tail *and* the existing heading-aware
  `MarkdownChunker` with **no synthetic structure** (Jira needed `## Comment`
  sections; Confluence pages carry their own `<h2>`/`<h3>`). The renderer only
  prepends `# <title>` + a metadata blockquote (space, ancestors, author,
  version) ahead of the converted body.
- **No depth-degrading fidelity.** Unlike a Jira ticket (deep ones went
  description-only to shed comments), a page *is* its body — a truncated page is
  just a worse page — so every crawled page is indexed in full; the budget cap
  is the only size lever.
- **Change detection uses `version.number`, not a timestamp.** Confluence bumps
  a monotonic integer `version.number` on every edit, a cleaner signal than the
  `updated` clock Jira had to use; the monitor stores it and re-indexes when the
  live number is higher. `source_modified_at` is still `version.when` (+
  `version.by` author). Otherwise the watch loop, monitored-set registry, and
  `confluence purge` mirror §2.16 exactly.
- **Parallel namespace, not shared "Atlassian" infra.** `Rtfm.Core/Confluence/`
  mirrors `Rtfm.Core/Jira/` (client, config+store, crawler, monitor, source
  key, renderer) rather than extracting a shared base. Consistent with RTFM's
  per-format converters (§2.5): independent and comprehensible beats
  prematurely-DRY. Genuinely shared bits are already shared
  (`EnvironmentExpansion`, `DocumentIngestor.IngestDocumentAsync`); extract more
  only if a third Atlassian product ever appears.

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
| OCR for PDF-embedded images (Phase 16) + standalone images (Phase 17) | `RapidOcrNet` (PP-OCRv5 via ONNX Runtime; models in-package) + SkiaSharp |
| SQL schema files (Phase 18) | none — built-in dialect-tolerant DDL scanner |
| Live DB schema pull (Phase 20) | `Microsoft.Data.SqlClient` + `Npgsql` via INFORMATION_SCHEMA |
| Email (`.eml`, `.mbox`) → markdown (Phase 24) | `MimeKit` (already shipped for MHTML) — quote/signature strip is hand-rolled |
| Jira Cloud pull (Phase 25) | none — `HttpClient` (REST v3, Basic auth) + `System.Text.Json`; rendered-HTML fields reuse the ReverseMarkdown tail |
| Confluence Cloud pull (Phase 26) | none — `HttpClient` (REST v1, Basic auth) + `System.Text.Json`; `body.view` HTML reuses the ReverseMarkdown tail *and* the heading-aware chunker |
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
> **2.19.5** (2.17.1 from Phase 0 was upgraded during Phase 18: its hybrid
> query 500'd — "read past EOF … .nvd" — on specific queries whose BM25 and
> kNN clauses each worked alone; 2.19.5 fixed it. 2.19 also ships RRF, but
> retrieval stays on the min_max normalization pipeline — switching is an
> option, not a need. `DocumentSearch` additionally falls back to lexical
> per-query if hybrid ever 500s again). `MimeKit` **4.17.0**, `AngleSharp` **1.5.1**,
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

*In progress — .NET tool packaging landed; feed publish + winget/brew still
open.* Both executables gained `PackAsTool`/`ToolCommandName`
(`rtfm`/`rtfm-mcp`)/`PackageId` + NuGet metadata; `dotnet pack` bundles the
full dependency closure — **including the RapidOcrNet models and every
platform's native runtime (ONNX Runtime, SkiaSharp, SqlClient SNI)** — into the
RID-agnostic `tools/net10.0/any/` payload, so an installed tool is
self-contained (only the embedder/reranker models still auto-download, as
before). The feared model-copy gotcha didn't materialize: the Core `<None
CopyToOutputDirectory>` items flow into the tool publish output automatically.
Packaging output goes to `artifacts/nupkg` (gitignored). One trim was needed —
ONNX Runtime's Android `.aar` + iOS `.xcframework` (~94 MB, unloadable by a
desktop tool) pushed the nupkg to ~215 MB, near nuget.org's 250 MB ceiling; a
`PackAsTool`-gated `RtfmTrimMobileRuntimeAssets` target in
`Directory.Build.props` drops those RIDs from the publish set → **123 MB**.
Validated end-to-end: isolated `--tool-path` install of `Rtfm.Cli`, then
`rtfm ping` / `index` / `search` (hybrid + rerank, ONNX loaded from the install
dir) / `purge` all correct. README gained an "Install as a .NET global tool"
section (two packages, install both — .NET tools don't chain-install; the bare
`rtfm-mcp` `.mcp.json` form). **Publish is CI-driven** (`.github/workflows/
release.yml`): a `v*.*.*` tag reuses ci.yml's cross-OS test matrix
(`workflow_call`) as a gate, guards that the tag matches
`Directory.Build.props` `<Version>` (the single source of truth), packs both
tools, and `dotnet nuget push --skip-duplicate` to nuget.org with the
`NUGET_API_KEY` repo secret. This sidesteps the local `Rtfm.Mcp` pack's DLL
lock (the §6 gotcha) entirely — CI has no running server; the lock only bites a
local `dotnet pack src/Rtfm.Mcp`. Package IDs kept as `Rtfm.Cli` / `Rtfm.Mcp`
(permanent on nuget.org). The `NUGET_API_KEY` repo secret **is configured**, so
a matching tag push publishes — `v1.1.0` (the Jira bare-HTML route, §2.5) is the
first real release. **Open:** §2.12's `vm.max_map_count` note still to fold into
the packaged-install story. Version flows from `Directory.Build.props` (1.1.0).
Winget + a Homebrew tap are **parked** as follow-on channels over the same
self-contained publish.

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

### Phase 17 — Standalone images (.png/.jpg/.jpeg) ✅ **Done**
The Phase 16 OCR engine, given a front door: architecture screenshots,
exported diagram bitmaps, and photographed whiteboards become indexable
documents.
**Done when:** a PNG whose knowledge exists only as pixels indexes and answers
a question about its content.

*Delivered:* the OCR engine extracted from `PdfConverter` into a shared
internal `OcrEngine` (one lazy instance per process, used by both routes), and
a small `ImageConverter`: filename stem as title + `[Image text] …` body; a
textless image still yields its title line so it stays visible in
`list_sources`. Detection by magic bytes (`\x89PNG`, `FF D8 FF` — content wins
over extension); `.png`/`.jpg`/`.jpeg` gate discovery. Pleasant side effect:
`.drawio.png` exports — previously skipped entirely — now index via OCR of the
rendered image (labels only; the true XML route remains the better path when
the plain `.drawio` exists). Verified live: a runbook screenshot OCR'd
character-perfect (including the CLI command `rtctl vip rotate --cluster eu`)
and "how do I rotate the VIP during failover" answers at #1 (1.00). 124/124
tests.

### Phase 18 — SQL schema files (.sql) ✅ **Done**
Entire DB schemas live in reference `.sql` files outside any codebase. Plain
text would index, but a *structural* parse is worth it: per-table chunks (the
Phase 15 granularity lesson, applied) and an explicit relation graph.
**Done when:** a real schema dump indexes and "which tables reference X"
answers from the right table's own chunk.

*Delivered:* `SqlConverter` — a dialect-tolerant DDL scanner (no parser
dependency; full SQL grammars are dialect quicksand and a reference file must
never fail conversion). Comment-stripping and statement-splitting respect
string literals and Postgres dollar-quoted bodies; `CREATE TABLE` bodies are
balanced-paren captured and split at top level. Extracted: columns (name,
type, PK/NOT NULL/UNIQUE/DEFAULT/auto flags — table-level `PRIMARY KEY (…)`
constraints mark their columns too), foreign keys (inline `REFERENCES` *and*
`ALTER TABLE … ADD CONSTRAINT`), `COMMENT ON TABLE/COLUMN` descriptions, and
secondary objects (views with definition snippets, indexes, `AS ENUM` types
with values, functions/triggers/sequences). Rendering: `## Table: X` per table
→ its own breadcrumbed chunk, with FK annotations on columns **and** a
computed **Referenced by** reverse line, so "which tables reference X" is
answerable from either side; plus a `## Relationships` overview and an
"Also contains: N data statements" tally. Anything unrecognized is counted,
never fatal; a file with no recognizable DDL falls back to a fenced ```sql
block (still lexically searchable). Verified on the real corpus: two
production **T-SQL** dumps (`dbo.` schemas, bracketed `[char](18)` types)
parse fully — 18 + 51 chunks — and "which tables reference the Bundle table"
answers from `dbo.Bundle`'s own chunk at **0.97** (vs the drawio all-entities
chunk scoring 0.00 — per-object granularity vindicated). This validation also
flushed out an OpenSearch 2.17 hybrid bug (see the §3 pin note / Phase 6
delivered note context): fixed by the 2.19.5 upgrade + per-query lexical
fallback. 133/133 tests.

### Phase 19 — Agent write-back: `save_document` ✅ **Done**
The workflow: "analyze this feature, identify the missing bits, and remember
them via rtfm." LLM-produced documents (analyses, gap lists, decision records)
become part of the corpus — retrievable by every future session.
**Done when:** a document saved over MCP is immediately findable by
`search_docs`, updates in place on re-save, and carries visible provenance.

*Delivered:* `GeneratedDocumentStore` in Core + the `save_document` MCP tool
(the server's first ingest-side services: `DocumentIngestor` + detector now in
Mcp DI). The derived-index model holds: the markdown is written as a **real
`.md` file** under `RTFM_GENERATED_DIR` (default
`LocalApplicationData/rtfm/generated/<project>/`) and ingested through the
normal convert→chunk→embed→index pipeline — the file stays the source of
truth and the folder can always be re-indexed. Same title + project → same
slug → same file → **replaced, not duplicated** (delete-by-query semantics do
the rest), so "update the analysis" is just re-saving the title. A provenance
line ("LLM-assisted document, saved by … on …") is prepended automatically —
without it, fresh generated content would outrank stale-but-authoritative
sources on recency (§2.13 B) with no disclosure; a duplicate leading H1 from
the agent is deduped. Contradiction detection runs on save (a generated
analysis disagreeing with the docs gets nominated). Tool description enforces
user direction ("remember this via rtfm") — never persist on the agent's own
initiative. Verified over raw stdio: save → 3 chunks + real file; a
gap-question hits the generated doc's `Missing bits` section #1; re-save same
title → `replaced=true`, chunks 3→2 (no accumulation); `list_sources` shows
it. `rtfm purge` drops its chunks; the files are sources and stay (delete the
folder to forget them). 140/140 tests.

### Phase 20 — Live DB schema connector (`.rtfmdb`) ✅ **Done**
A `.sql` dump is a photograph; a connector descriptor is a window. A small
JSON file in the docs folder tells RTFM to pull the schema from a live
database at ingest time and render it through the Phase 18 engine — the
schema can never go stale.
**Done when:** a `.rtfmdb` pointing at a live database indexes its schema with
per-table chunks and answers "which tables reference X".

*Delivered:* `DbDescriptor` (`provider`, `connectionString`, optional `name` /
`schemas` filter) + `DatabaseSchemaConverter`, sharing the schema model and
renderer extracted from `SqlConverter` into `SqlSchemaModel.cs`
(`SqlSchemaRenderer`) — both routes produce identical per-table chunks, FK
annotations, and the Referenced-by reverse index. **Secrets never sit in the
file:** connection strings support `${ENV_VAR}` placeholders, expanded at
ingest, failing loudly on missing vars. Providers: **sqlserver**
(`Microsoft.Data.SqlClient` 7.0.2) and **postgres** (`Npgsql` 10.0.3);
metadata via portable `INFORMATION_SCHEMA` queries (tables/columns with
NOT NULL + defaults, PKs, composite-safe FKs grouped per constraint, views);
table/column *descriptions* via provider-specific catalogs
(`pg_description` / `sys.extended_properties` MS_Description), best-effort.
System schemas filtered. Stays inside the §2.4 batch model: the pull happens
when the descriptor is ingested — every `rtfm index` run refreshes it, watch
re-pulls when the file changes — and `source_modified_at` is the pull time,
with a provenance line ("Live schema pulled from … on …") in the markdown.
Verified live against a throwaway Postgres 16 container: comments, FK flags,
Referenced-by, and the view all extracted; "which tables reference the
accounts table" hit `Table: public.accounts` at **1.00**. SQL Server path is
implemented against the same INFORMATION_SCHEMA standard but live-validated
only for Postgres so far — exercise it on a real MSSQL box when convenient.
147/147 tests.

### Phase 21 — Retrieval UX quick wins (first-user feedback) ✅ **Done**
The first real dogfooding session (2026-07) produced a ranked feedback list;
this phase is the cheap-and-high-leverage half. Six independent items, no
mapping changes:
- **Project discovery** — a `list_projects` MCP tool (per-project doc/chunk
  counts + recency, backed by the existing `StatusService` aggregation), and
  unscoped `list_sources` returns that per-project summary instead of dumping
  every document across projects (a `full` arg keeps the old behavior). The
  observed failure: 184 sources returned, ~170 of them another project's
  noise, and the project name only discoverable by reading the dump.
- **`ping` MCP tool** — cheap liveness probe (OpenSearch reachability with a
  short timeout) so an agent can check the stack in seconds before committing
  to an expensive call; doubles as restart verification.
- **PDF title sanitation** — reject metadata titles that look like filenames
  (`index.html`) or carry doubled-character runs (`IIDD EEppiicc`, a text-run
  duplication artifact); fall back to first top heading, then filename stem.
- **Chunk-neighborhood fetch** — `search_docs` hits expose their chunk
  `ordinal`; `get_document` takes `around_ordinal` + `radius` to fetch just
  the section around a hit instead of all-or-nothing whole-document reads.
- **Idempotent notes** — note ids become deterministic (hash of project +
  text + anchor, mirroring `ContradictionPair.Id`), so a timeout-and-retry
  `add_note` upserts instead of double-adding.
- **Pathless-note guidance** — unanchored project-level notes already work;
  the `add_note` description must say so (decisions that live above any one
  doc shouldn't get an arbitrary anchor).
**Done when:** unscoped `list_sources` answers "what projects exist" in one
small response; `ping` reports both the up and down cases quickly; a PDF with
a filename/doubled-run metadata title indexes under a sane title; a search
hit's section is fetchable via ordinal + radius without pulling the whole
doc; calling `add_note` twice with identical text yields one note; tests
cover the new pure logic.

*Delivered:* `list_projects` + `ping` in Mcp (`StatusService` reused for the
former — no new Core queries; the latter wraps `PingAsync` in a 5s cap with
an actionable start-the-stack error), summary-first unscoped `list_sources`
(per-project rollup + note when >1 project; single-project corpora fall
through to the listing; `full=true` restores the dump),
`PdfConverter.SanitizeMetadataTitle` (filename/path-shaped and
doubled-character-run titles rejected → first heading → filename stem — a
PDF now never titles itself `index.html`), chunk-neighborhood fetch
(`SearchHit.Ordinal` flows from `_source` through `search_docs` hits;
`get_document(around_ordinal, radius)` range-filters on the ordinal sort and
marks the result `partial`), and deterministic note ids
(`NotesStore.DeterministicId` = SHA-256 of project|anchor|text, 16 hex chars
mirroring `ContradictionPair.Id` — retried `add_note` upserts; identical
retry only refreshes `CreatedAt`). `add_note`'s description now names
unanchored project-level notes as the right choice for doc-transcending
decisions. Verified live against the real 2-project corpus: unscoped
`list_sources` answers in a 2-row summary (was the 184-source dump);
windowed `get_document` on a 61-chunk schema doc returns 3 chunks / 1.0 KB
vs 37.5 KB full; double `add_note` → one note in `rtfm-notes`; ping up
(yellow, instant) and down (refused endpoint, fast, actionable) both
correct; raw-stdio smoke shows 11 tools and pure JSON-RPC stdout. 159/159
tests.

### Phase 22 — Contradiction lifecycle (first-user feedback, part 2) ✅ **Done**
The precision half of the feedback: 15 nominations, ~2 real. Four items:
- **Template suppression** — chunks whose normalized text appears in 3+
  documents of a project are template boilerplate by definition (PRD headers,
  "Document Owners" tables); suppress them as either side of a nomination.
  Implementation sketch: a `content_hash` keyword field stamped at ingest
  (hash of the already-computed normalized text) + a cardinality check at
  detection time. Mapping addition — new field only populates on re-index,
  acceptable.
- **Supersession labeling** — pairs already store both `source_modified_at`
  values (side A = newer); classify at nomination: a large date gap ⇒
  `kind: "likely-supersession"` ("newer doc disagrees with older — confirm
  and prefer newer") vs `kind: "contradiction"`. This turns the observed
  MSP role-vs-flag case from an open investigation into a one-step confirm.
- **Dismiss / resolve** — `dismiss_contradiction` + `resolve_contradiction`
  MCP tools (+ CLI). Resolve composes with `NotesStore`: a confirmed pair
  becomes an override note and the pair is closed. Dismissals must survive
  re-ingest (pairs are dropped + re-nominated when either doc re-ingests;
  the deterministic pair id makes a tombstone check reliable) — without
  this, every dismissed pair resurrects on the next `rtfm index`.
- **Note-precedence live verification** — the Phase 13 promise (note ranks
  above / annotates the stale passage) was validated once; re-verify against
  a real dogfooding note + query, and if a relevant note misses the 0.6 kNN
  floor for natural question phrasings, tune the floor with evidence.
- **Explicitly deferred:** opposing-polarity detection (NLI). A third model
  against the locked "RTFM nominates, the LLM judges" decision (§2.13) —
  only revisit if template suppression + supersession labeling leave
  precision unacceptable.
**Done when:** re-indexing the real corpus produces zero template-boilerplate
nominations while the planted `admin`/`super-admin` pair survives; a
date-gapped pair carries the supersession label; a dismissed pair stays
dismissed across a full re-index; a resolved pair yields exactly one note and
disappears from open nominations.

*Delivered:* **Template suppression is two-granularity** — the planned
chunk-level `content_hash` (stamped at ingest; nominations dropped when either
side's hash spans ≥3 docs, one aggregation per doc-batch) *plus* a line-level
pass the real corpus forced: the surviving noise was template **variants** —
the same "Document Owners"/"PRD Approval" tables in 3 docs, each copy differing
by a few characters ("Pod Owner" vs "Pod Ow"), invisible to exact whole-chunk
hashing (measured trigram Jaccard 0.655 — no near-dup threshold separates this
from real content either). So chunks also carry `line_hashes` (normalized
lines ≥16 chars, deduped), and a nomination is suppressed when ≥3 of the lines
its two sides share verbatim span ≥3 docs *and* those are the majority of
everything shared — the planted pair shares zero lines, so it survives by
construction, and 2-doc copies (supersession material) stay eligible. Both
fields ride `RtfmIndex.MappingAdditionsJson`, PUT idempotently by
`EnsureIndexAsync` onto pre-existing indexes (new `PutMappingAsync` /
`UpdateAsync` gateway ops); hashes populate on re-index, so the *first*
re-index after upgrading suppresses only partially — the second has full
coverage. Real-corpus result: 76 open pairs → 64, a scripted sweep of every
survivor found **zero** template violations at either granularity, and
isos-admin's remainder is exactly the real content (the MSP role-vs-flag
disagreement the dogfooding session flagged). kabuk's residue is recurring
meeting-notes near-dups — a *different* noise mode (series, not boilerplate),
left for the LLM judge. **Supersession labeling:** `kind` stamped at
nomination (30-day gap ⇒ `likely-supersession`); the real corpus yields none
(exports share mtimes) — validated with a planted 5-month pair. **Lifecycle:**
`status`/`resolved_note_id`/`status_changed_at` on pairs; `rtfm contradictions
[--closed] | dismiss <id> | resolve <id> --note <text>` and the
`dismiss_contradiction`/`resolve_contradiction` MCP tools (descriptions
enforce explicit user confirmation, mirroring `add_note`). Re-ingest now drops
only **open** pairs (closed ones are tombstones; their deterministic ids also
block re-nomination — verified: a dismissed and a resolved pair both survive
full re-indexes), document deletion still drops everything, purge drops the
project's pairs and notes. Resolve composes with `NotesStore`: one note,
anchored to the older side. **Note-precedence re-verification** found the
promised failure: a real dogfooding note scored 0.46–0.58 against natural
phrasings of its own topic (missing the 0.6 kNN floor; it only reached the
user as an anchored annotation), while irrelevant notes clustered at
0.33–0.37 — `NotesStore.MinSearchScore` lowered to **0.45** on that evidence,
after which the note ranks #1 as an attributed override and unrelated queries
stay note-free. MCP smoke: 13 tools, stdout pure JSON-RPC, closed pairs carry
kind/status over the wire. 168/168 tests.

### Phase 23 — Live data gateway: `query_database` ✅ **Done**
Phase 20 indexed the *schema*; this reads the *data*. An agent that can query a
DB but doesn't know the schema writes garbage SQL — RTFM already has the
schema, so the pairing (search schema → write SQL → run it) is the point. The
decision and its safety model are §2.15; this phase is the build.
- `list_databases(project?)` — discovery, mirroring `list_projects`.
- `query_database(database, sql, max_rows?, project?)` — capped, returns a
  markdown table + a `truncated` flag; read-only unless the descriptor opts in.
- `rtfm db list` / `rtfm db query <name> "<sql>"` for dogfooding.
**Done when:** a `.rtfmdb` with a `query` block answers a real data question
over MCP; one without it is refused as schema-only; a write against a
read-mode descriptor does not persist and is reported as an error; a descriptor
with `allowWrites` can write; the row cap truncates with a visible flag.

*Delivered:* `Rtfm.Core/Database/` — a new namespace, because this is the live
data plane, not conversion and not a derived index. `DbDescriptor` moved here
out of `DatabaseSchemaConverter.cs` and gained the optional `query` block
(`connectionString?`, `maxRows` = 500, `timeoutSeconds` = 10); **env expansion
became lazy** (`ResolveConnectionString` / `ResolveQuery`) so the indexing
process and the querying process can hold different secrets — the eager
parse-time expansion of Phase 20 would have made the MCP server demand the
schema-pull credential it has no business holding. `DatabaseQueryService`
guards reads per §2.15 (Postgres `SET TRANSACTION READ ONLY`; SQL Server
transaction + unconditional rollback, since its DDL is transactional too) and
takes a `DatabaseInfo`, reading/parsing the descriptor itself so hosts never
touch a connection string (`DbDescriptor` stays `internal`).
`DatabaseRegistry` discovers `*.rtfmdb` under each manifest's
`OpenableFolder`, resolving by filename handle then friendly name.
**Course-correction during the phase (worth keeping):** the first cut enforced
SQL Server read-only by probing the login and refusing anything that could
write. The owner's "I only run this on local test DBs" flushed out that this
was backwards — a local MSSQL connects as `sa`, so the probe would have refused
*every* query on that provider, i.e. the enforcement broke the only use case it
served. Replaced by the rollback guard, which holds even on a superuser
connection (verified), plus `allowWrites` for deliberate write access. The
lesson generalizes: an enforcement whose false-positive rate is 100% against
the real audience isn't strict, it's broken.
**The bug live validation caught (unit tests could not):** the reader was left
open across `transaction.RollbackAsync`, and Npgsql allows one active command
per connection — so *every successful SELECT* came back as "A command is
already in progress". The write path masked it: `INSERT` failed at
`ExecuteReaderAsync` before a reader ever opened, so the 25006 refusal looked
perfect while reads were uniformly broken. The reader is now scoped to close
before the rollback.
Verified live against a throwaway Postgres 16, over **both** the CLI and raw
MCP stdio: 15 tools advertised, stdout pure JSON-RPC; `list_databases` reports
schema-only / read-only / read+write correctly; a join with NULLs renders as a
markdown table; 25 rows truncate at the descriptor's `maxRows: 10` with
`truncated: true` + a narrow-your-query note; the schema-only descriptor is
refused; and the schema-first workflow holds (`search_docs` hits `Shop DB >
Table: public.customers` #1). The read/write split, both ways round: against a
read-mode descriptor **using superuser credentials**, `INSERT` and an
unqualified `DELETE FROM customers` both → `25006 … in a read-only
transaction`, and all 5 rows survived (checked in the DB, not just the tool
response); against an `allowWrites` descriptor the `INSERT` returned
`rowsAffected: 1` and the row landed. 182/182 tests.
*Smoke-harness note:* piping requests into `rtfm-mcp` from a file makes the
transport hit EOF instantly and shut down while handlers are still running —
responses are dropped and stdout looks empty. Hold the pipe open
(`( cat requests; sleep 12 ) | dotnet rtfm-mcp.dll`). A harness artifact, not
a server bug: real clients keep stdin open.
**SQL Server live-validated (2026-07-16) — and the "Open" asymmetry was hiding
a real bug.** Run against a throwaway SQL Server 2022 as `sa`, the rollback
guard held exactly as designed (DDL *is* transactional; nothing persisted), but
the **reporting** half was broken: read-mode outcomes keyed off
`DbDataReader.RecordsAffected > 0`, and **DDL reports `-1` there exactly like a
SELECT does** — so a rolled-back `CREATE TABLE` came back as "OK — no rows
returned" / `success: true`. Precisely the silent success §2.15 forbids, and
worse than a failure: the agent proceeds believing it wrote. `INSERT` reports
`1` and *was* caught, which is why the guard looked correct when it was first
validated — the DML probe couldn't see the DDL hole. Fixed in
`EvaluateSqlServerReadOutcome` (a pure seam, unit-tested): a read-mode statement
is a confirmed read only if it **came back with a result set**; no result set ⇒
reported as rolled back. That over-reports a bare `PRINT`/`SET` (one wasted
agent retry) and under-reports nothing. Known residual: a batch that selects
first and writes later (`SELECT 1; CREATE TABLE …`) still reports the read —
closing it needs SQL parsing (defeated by CTEs/stacked statements, §2.15) or a
VIEW SERVER STATE DMV (the login-permission trap this phase already learned).
**Postgres was correct all along** — 25006 comes from the engine as a real
`DbException`, so it never had a reporting half to get wrong.
*The generalizable lesson:* the rollback and the report are two independent
halves of one guarantee, and a live probe that only exercises DML proves only
half. Verified end-to-end afterwards on both surfaces (CLI exit 1 + MCP
`success:false`, stdout pure JSON-RPC), with the DB itself as ground truth: the
read-only `CREATE`/`INSERT`/`DROP` left no trace, while an `allowWrites`
descriptor's `CREATE`/`INSERT` both landed.

### Phase 24 — Email chains (`.eml`, `.mbox`) ✅ **Done**
Decisions get made in threads and never make it back into Confluence. An
exported chain is often the only record that `admin` became `super-admin` — and
unlike every other input, each message carries a **real author and a real
`Date:` header**, so §2.13's recency reasoning finally has trustworthy input
(the current corpus is exports that share mtimes, which is why Phase 22's
supersession labeling finds nothing real to label).

**The parser is nearly free; the chunking is the whole phase.** MimeKit already
does MIME + quoted-printable for the MHTML route (§2.5), so `.eml` is the same
library and the same strip→ReverseMarkdown tail. Build the format first only to
get to the two problems that actually decide quality:

- **Thread = document, message = chunk.** Email has no headings, so
  `MarkdownChunker` would degrade to blind paragraph windows and breadcrumbs
  would collapse. Synthesize the hierarchy from thread structure instead:
  breadcrumb `<subject> > <date> <sender>`. This is the Phase 15/18 granularity
  lesson a third time — per-message chunks beat one thread-shaped blob, and a
  question about what was decided in message 7 should hit message 7.
- **Quote-stripping is load-bearing, not polish.** Reply #8 quotes messages
  #1–7 inline, so a naive index stores the first message ~8 times. That is
  precisely the contradiction detector's nomination signature (near-identical
  text, different dates, §2.13), and **Phase 22's template suppression will not
  save us** — it keys on content repeated across ≥3 *documents*, while this is
  one document duplicating itself. Strip `>` prefixes,
  `-----Original Message-----`, `On <date>, X wrote:`, and `<blockquote>` in
  the HTML part.
- **Signature/disclaimer stripping, after de-quoting.** (The plan had this
  backwards: it assumed quoted senders' signatures would survive as orphaned
  fragments and had to be cut first. They don't — quote-stripping cuts from the
  first boundary to the *end* of the body, disposing of the quoted history
  wholesale, so the signature pass only ever sees the author's own trailer.)
  Targets:
  the RFC 3676 `-- ` delimiter (reliable when present, and Outlook usually
  omits it, so it can't be the only rule), corporate legal disclaimer blocks
  (the worst offender — 80+ words appended to *every* message), `Sent from my
  iPhone`-class footers, and a trailing-contact-block heuristic (a run of short
  lines that are mostly phone numbers, URLs, and titles, with a name matching
  `From:`). Tracking pixels and logo bugs are already free — Phase 16's icon
  filter skips anything under 80 px on the short side.
- **Not built now:** corpus-frequency signature detection. Phase 22's
  `line_hashes` would identify signature blocks *empirically* (the same 6 lines
  under every message from one person is definitionally repeated content), but
  it needs a second pass over an already-indexed corpus, and the deterministic
  rules should get ~90% there. Same "don't pre-build it" posture as §2.5's Open
  XML table fallback — revisit only if real exports prove messier.
- **`.msg` (Outlook CFB) is out of scope** for this phase; it needs a separate
  container library. Drag-drop to `.eml` is the documented workaround.
- **Privacy is explicitly the user's problem** — consistent with §intro (per-dev
  local tool) and §2.14 (the user chooses what to index). No opt-in gesture, no
  content gating. Sanitization here is about *retrieval quality*, not secrecy.

> **`.eml` collides with the MHTML sniffer — MHTML *is* an email container.**
> §2.5's detection routes on `MIME-Version:` / `multipart/related`, which a
> `.eml` file matches happily, so email would silently ride the Confluence
> route and convert as a malformed web page. The signal that separates them:
> a real message carries `From:` + `To:` + `Subject:` + `Date:` headers and is
> usually `multipart/alternative` or `multipart/mixed`, while a Confluence
> export is `multipart/related` with `Content-Location` parts and no
> conversational headers. **Check for message headers before the MHTML rule**,
> and keep a fixture of each so the two can never re-cross.

**Done when:** a 10-message exported thread yields ~10 chunks (not ~55), each
breadcrumbed by subject + date + sender; a question about a decision made
mid-thread hits that message's own chunk; signatures and legal disclaimers do
not appear in retrieved text; and indexing a thread produces zero
self-duplication nominations in `rtfm contradictions`.

*Delivered:* `EmailConverter` (a §2.5 front end — no tail change) +
`EmailSanitizer` (the quote/signature passes as an `internal static` seam, unit
-tested as pure string logic). MimeKit was already shipped for MHTML, so **no
new dependency and no §3 pin**: `MimeMessage.Load` for `.eml`, `MimeParser` with
`MimeFormat.Mbox` for `.mbox`. A file becomes one document, each message a
`## <date> <sender>` section under the subject's `#`, so the existing chunker
yields one chunk per message with the `subject > date > sender` breadcrumb —
**no chunker change**. Plain text is preferred over HTML (it quote-strips far
more reliably); HTML-only messages route through the shared tail first, where
ReverseMarkdown renders `blockquote` as `>` and the same text passes then apply.
Body lines starting with `#` are escaped — unescaped, one would be read as a
heading and shatter the per-message structure. Thread title strips `Re:`/`Fwd:`
prefixes; `SourceModifiedAt` is the newest message (per §2.15-style scoping
decision: per-message dates would need a chunk-level contract change, and the
case that drives §2.13 is thread-vs-document recency — contradiction detection
excludes same-document pairs anyway).

**Two findings the plan got wrong, both caught live:**
- **512 bytes is not enough to sniff email.** `FormatDetector` peeked 512
  bytes; the real corpus's `.eml` carries a `Received:`/`DKIM-Signature` chain
  that puts `Subject:` at **byte 1540**. The email rule now reads an 8 KB
  header window — *only* that rule, because widening it for every rule would
  let a stray `<html` deep inside a CSV beat its own extension. The peek also
  moved to `ReadAtLeast`: a single `Read` may return short and would truncate
  the window arbitrarily.
- **The email rule is ordered before MHTML and separates on recipients.**
  Requiring `From:` **and** `To:`/`Cc:` together is what distinguishes a message
  from a Confluence export (which has a `Subject:` and a `Date:` but no
  recipients). Anything ambiguous falls through to MHTML — the pre-Phase-24
  behavior. Both directions are pinned by tests.

**Corrected in 1.5.1 — quote-stripping is per-container, and `.eml` splits.**
1.5.0 shipped a documented "known limitation": a lone `.eml` lost its history,
on the assumption that chains are exported per message. **That assumption was
wrong for the actual export habit** — Outlook's "Save as" writes the *whole
thread* into one `.eml`, so stripping the quotes deleted every message but the
newest. The real corpus thread grew from 2 indexed chunks to **1** as replies
were added: appending a reply shrank the indexed content, which is exactly
backwards. The rule is now container-specific, and the distinction is the whole
fix:
- **`.eml` = one MIME message.** Its quoted history is the *only* copy of the
  earlier thread in the file, so `EmailSanitizer.SplitThread` divides at the
  boundaries and keeps every segment, oldest first, attributed by the sender and
  date its inline header block declares. Quoting in a threaded body is linear,
  not repetitive — each message appears exactly once — so splitting duplicates
  nothing.
- **`.mbox` = many MIME messages.** Siblings already hold those messages, so the
  quoted copy really is redundant and `StripQuotedHistory` still removes it. Not
  stripping here would index a ten-message chain's first message ten times.

Real exports use two Outlook dialects, sometimes in the same file: a
`________` divider + `From:`/`Sent:`, and a bare `From:`/`Date:` with no
divider — and **no `>` prefixes at all**, so `>`-based detection alone would
have found nothing. A bare `From:` only counts as a boundary when a
`Sent:`/`Date:` follows within a few lines, which keeps prose ("From: the design
review we learned…") from splitting a message. Gmail's `On … wrote:` + `>`
form is handled too. Dates parse from both `Friday, July 17, 2026 7:46 PM` and
`Friday, July 17, 2026 at 03:24` (the ` at ` defeats `TryParse` and is
normalized away). Per-message dates ride the breadcrumb; `source_modified_at`
remains the thread's newest date, unchanged.

> **Contract moved (per `tests/CLAUDE.md`).** 1.5.0's
> `Drops_quoted_history_signature_and_contact_block` asserted that a `.eml`'s
> quoted history was absent. That expectation was the bug, and the test was
> deliberately inverted to
> `Eml_keeps_quoted_history_as_its_own_message_section`. Signature/disclaimer
> stripping is unchanged and still applies **per segment**.

**Remaining limitation:** if the same thread is exported more than once as it
grows (two files, different names, overlapping messages), the shared messages
are indexed from both. Content loss is strictly worse than duplication, so this
is the right trade — and Phase 22's `line_hashes` suppression already covers
the contradiction-nomination half once copies span ≥3 docs. Outlook's native
`.msg` (CFB, not MIME) remains unsupported.

**Validated:** unit fixtures for both containers, every quote-boundary dialect
(`On … wrote:` including the two-line wrap, `-----Original Message-----`,
Outlook's `From:/Sent:/To:` block, `____` divider), and the sanitizer's
non-actions (a plain "Thanks,\nAlex" sign-off survives; a `From:` line that
opens a sentence is not a boundary). Live: a three-message thread exported
per-message → 3 docs / 3 chunks with exact `subject > date > sender`
breadcrumbs; "what is the default user role for a new tenant" ranks the newer
`super-admin` correction #1 (1.00) with the stale `admin` claim still retrieved
at #2 — §2.13 B working as designed, conflict visible rather than hidden. Every
other format in the real corpus re-detects unchanged (4 Mhtml, 1 Html, 4
Drawio, 2 Sql, 1 Pdf). The corpus turned out to **already contain a real
`.eml`** (a PMM permissions thread), which converts cleanly and preserves the
inline "Answered by <name>:" annotations — the tribal knowledge that motivated
the phase. 211/211 tests.

**Open — contradiction detection does not reach email, and floor-tuning is not
the fix.** Two measurements, both against the 0.75 nomination floor:
- *Message vs message* — the planted `admin` vs `super-admin` disagreement
  scored **0.7242**. Marginal; conversational text scores structurally lower
  than statement-to-statement prose, the same effect Phase 22 measured for
  notes (floor lowered 0.6 → 0.45 *on evidence*).
- *Message vs schema* — the real corpus email states that PMM's
  `PartyExtension.Value` is `nvarchar(max)` while noting the schema export shows
  `varchar(0)`; a genuine, already-known disagreement with `PMM_rev.sql`. Best
  similarity to any schema chunk: **0.5267** — and it *was* the right table
  (`dbo.PartyExtension`, ranked above its siblings), so relative ranking holds
  while the absolute scale collapses. Same shape as the Phase 15 drawio finding.

So a single global floor cannot serve both prose-vs-prose and prose-vs-schema:
0.53 would nominate essentially everything. Dropping the floor to catch either
case would regress the precision work Phase 22 just finished on the same corpus.
Left unnominated deliberately. If this is worth closing later, the lever is
per-pair-type floors or a different comparison basis — **not** one lower number.
Retrieval is unaffected: `search_docs` answers the same question at 1.00.

### Phase 25 — Jira integration: on-demand ticket pull + polling watch (§2.16) ✅ **Done**
The first source RTFM *pulls* over an authenticated API rather than reading from
a folder. A configured Jira workspace is pulled by ticket key, tickets and their
linked neighbours are indexed into the current project as thread-granular chunks,
and a poll loop keeps a monitored set fresh. The decision + safety model is
§2.16; this phase is the build, in four verifiable steps (resist pulling later
steps forward).

- **Step 1 — `JiraClient` + config + index one ticket.** `rtfm jira config
  --url <workspace> --email <you> [--token-env JIRA_TOKEN] [--project <name>]`
  stores a per-project descriptor (URL + email + `${ENV}` token ref, §2.16) and
  verifies auth via a read-only `GET /myself`. `rtfm jira index <KEY>` pulls the
  ticket (`expand=renderedFields`), renders `# KEY: summary` + a metadata
  blockquote + `## Description` + `## Comment by <author>, <date>` per comment
  (rendered HTML → shared ReverseMarkdown tail; machine dates/authors from raw
  `fields.comment`, joined by comment id), and ingests it under `source_path`
  `jira://KEY` via a new markdown-string ingest path (`DocumentIngestor
  .IngestDocumentAsync`, no file on disk). *Done when:* `rtfm jira index <KEY>`
  indexes one ticket as breadcrumbed chunks (`KEY: summary > Comment by …`),
  `source_modified_at` = the ticket's `updated`, and `search_docs` answers a
  question about the ticket's content.
- **Step 2 — graph traversal.** Follow structured issue links (and seed-only
  `AEXP-\d+` text mentions when `--follow-mentions`) breadth-first to
  `maxDepth`, bounded by the `maxTickets` budget with depth-degrading fidelity
  (seed = full; deeper = description-only), circular refs killed by a visited
  set, dropped work logged. `--dry-run` prints the expansion without indexing.
  *Done when:* a seed in a dense neighbourhood indexes a bounded set, no
  infinite loop, drop-count reported.
- **Step 3 — monitored-set registry + `rtfm jira watch`.** Persist the expanded
  key set + each ticket's last-pulled `updated` (manifest-shaped, per project);
  poll `key in (monitored) AND updated >= <since>` on `pollSeconds`, re-index
  deltas. Separate command from `rtfm watch` (poll vs. FileSystemWatcher, §2.16).
  *Done when:* editing a monitored ticket in Jira is reflected within one poll
  cycle.
- **Step 4 — purge.** `rtfm jira purge <KEY> | --all [--project]`: stop
  monitoring + delete-by-query the `jira://…` chunks + drop the registry entry,
  so monitored tickets don't pile up. *Done when:* a purged ticket vanishes from
  `search_docs` and stops being polled.

Zero MCP-tool changes: once indexed, tickets are ordinary chunks that
`search_docs` / `get_document` / contradiction detection already serve.

*Step 1 delivered.* `Rtfm.Core/Jira/` — `JiraConfig` (+ `JiraConfigStore`,
per-project descriptor under `LocalApplicationData/rtfm/jira`, `${ENV}` token
ref resolved lazily via the shared `EnvironmentExpansion` extracted from
`DbDescriptor`), `JiraClient` (read-only Cloud REST v3, Basic auth, `GET`-only
by construction — `FetchIssueAsync` + a `/myself` `VerifyAuthAsync`; `internal`
handler seam for tests), `JiraModels` (+ `JiraDate` for the colon-less zone
offset), `JiraDocumentRenderer` (issue → thread-granular markdown; rendered-HTML
description/comments reuse the `HtmlToMarkdownConverter` tail, comment headings
escaped), and `JiraSource.Key` (the `jira://KEY` builder, the PathNormalizer
analog that must skip `Path.GetFullPath`). Ingest via the new
`DocumentIngestor.IngestDocumentAsync` (markdown-string path, no file on disk;
the shared chunk→embed→index→detect tail was extracted so the file and Jira
routes share it). CLI: `rtfm jira config|index|list`. Verified live against
`internationalsos.atlassian.net`: `config` auth-checks read-only; `jira index
TOD-3112` → 5 chunks under `jira://TOD-3112`, breadcrumb `TOD-3112: … > Comment
by <author>, <date>`, `source_modified_at` = the ticket's `updated`, 100%
embedded; `search` hits it #1 (1.00); **re-index stays 5 chunks** (the
synthetic-key delete-by-query round-trips); `purge` removes all 5. 239 tests
(20 new).

*Step 2 delivered.* `JiraCrawler` (BFS from the seed, cycle-safe via a visited
set, fetch-failures skipped-not-fatal) with the three-cap leash from §2.16:
`maxDepth` (frontier tickets indexed but not expanded), a hard `maxTickets`
budget (the remainder reported as `Dropped`, never silent — §5), and
depth-degrading fidelity (`FetchIssueAsync(includeComments:)` — the seed pulls
comments, deeper tickets are description-only). Edges: issue links + `parent` +
`subtasks` from the issue fields, **epic/story children via a `parent = "KEY"`
JQL search** (the finding — an epic's stories are not a field; `SearchIssueKeysAsync`
paginates the new `/search/jql` `nextPageToken` endpoint), and — seed-only,
opt-in (`--follow-mentions`) — `KEY-\d+` text mentions **validated against real
project keys** (`FetchProjectKeysAsync`, so `UTF-8`/`SHA-256` don't 404-chase).
CLI: `jira index <KEY> [--depth N] [--max-tickets N] [--follow-mentions]
[--dry-run]` (flags override the stored config's defaults per run; `--dry-run`
prints the crawl plan table + leash report without indexing or touching
OpenSearch). Verified live on the real epic **AEXP-221**: `--dry-run --depth 1`
previews exactly 13 tickets (epic + parent UNICORN-36 + 11 children); `--depth 2
--max-tickets 15 --dry-run` hits the budget and reports "pulled 15, 9 more
discovered but not followed" (depth-2 frontier reached cross-project links,
cycle back to the epic handled); a real `--depth 1` indexed 13 tickets / 30
chunks / 100% embedded and the *child* AEXP-222 ("PAM v1 DB assessment")
answers a content query #1 (1.00). 243 tests (24 new).

*Step 3 delivered.* The monitored-set registry + polling watch. `JiraMonitorStore`
(per-project `JiraMonitor` of `MonitoredTicket{key, lastUpdated, full}` under
`LocalApplicationData/rtfm/jira/monitor` — a subfolder so it never collides with
the config files' hashed names); `jira index` records its crawled set there
(fidelity preserved: only the depth-0 seed is `full`). `rtfm jira watch
[--interval <s>] [--once]` polls: `JiraClient.FetchUpdatedAsync` batches
`key in (…)` searches for each ticket's live `updated`, `JiraMonitor.SelectChanged`
(pure, unit-tested) returns the keys whose live stamp is newer than stored (or
never stamped; a key missing from the poll is left alone), and each is re-pulled
at its stored fidelity → re-rendered → `IngestDocumentAsync` (delete-by-query
replace) → its stamp updated. **Change detection is per-ticket `DateTimeOffset`
comparison, not a JQL date literal** — timezone-proof and second-precise, and
the first poll after a restart *is* the catch-up (compares against the stamps
saved at index time). The monitor is reloaded each poll so a concurrent `jira
index` is picked up without a restart; `--interval` floors at 30 s. Verified
live: index (epic bounded to 4) → 4 monitored; `watch --once` → "polled 4 — no
changes"; backdating one stored stamp (simulating a remote change without
writing to Jira) → next poll re-indexes exactly that ticket; the poll after →
"no changes". 246 tests (3 new).

*Step 4 delivered (phase complete).* `rtfm jira purge <KEY> | --all [--project]
[--yes]`: a single key deletes just that ticket's chunks (delete-by-query
scoped to `source_path` **and** `project`, so a ticket shared with another
project isn't collaterally removed), drops its contradiction pairs
(`RemoveForPathAsync` on the raw `jira://KEY` — no `PathNormalizer`, which would
mangle the URI), and removes it from the monitored set; `--all` prefix-deletes
every `jira://` doc in the project, drops the monitored tickets' pairs, and
clears the monitor (confirms interactively, refuses without `--yes` when
redirected — the `rtfm purge` pattern). And the *general* `rtfm purge
<project>` now finishes the job: Jira chunks already carried the `project`
keyword (so they were always deleted), and it now also drops the project's Jira
**config + monitor** so nothing lingers, surfacing both on the pre-delete
"what's on the block" line. Verified live: `jira purge <KEY>` removed one
ticket's 2 chunks + dropped it from the set (search no longer finds it,
monitored 4→3); `jira purge --all` cleared the remaining 8 chunks + the monitor
(`jira watch` then reports "nothing to watch"); and `rtfm purge <project>`
reported "Jira config + monitor removed". 246 tests (purge is I/O — validated
live, not unit, matching `PurgeCommand`'s existing coverage). **Phase 25 done:
the first source RTFM pulls over an authenticated API, indexed, traversed,
watched, and purged.** Version bump 1.5.1 → 1.6.0 (additive phase) pending at
release.

### Phase 26 — Confluence integration: page pull + deep traversal + polling watch (§2.17) ✅ **Done**
Confluence, the Jira model applied to a wiki of pages (§2.17). A configured
workspace is pulled by page (URL/id) or space, the page's descendant tree and
in-body links are followed into the current project, and a poll loop keeps a
monitored set fresh on `version.number`. Four verifiable steps, mirroring
Phase 25.

- **Step 1 — `ConfluenceClient` + config + index one page.** `rtfm confluence
  config --url <ws> --email <you> [--token-env CONFLUENCE_TOKEN] [--project
  <name>]` stores a per-project descriptor and verifies auth (`GET
  /wiki/rest/api/user/current`). `rtfm confluence index <URL|id>` pulls the page
  (`expand=body.view,space,version,ancestors,children.page`), renders `#
  <title>` + a metadata blockquote (space · ancestors · author · version) + the
  `body.view` HTML through the shared ReverseMarkdown tail (its own headings
  drive chunking), and ingests it under `confluence://{pageId}` via
  `DocumentIngestor.IngestDocumentAsync`. *Done when:* `index <page URL>` indexes
  one page as heading-breadcrumbed chunks, `source_modified_at` = `version.when`,
  and `search_docs` answers a question about it.
- **Step 2 — traversal.** Follow child pages + in-body `/pages/{id}/` links
  breadth-first to `--depth`, bounded by `--max-pages`, cycle-safe, dropped
  links reported; `--dry-run` prints the plan; `--space <KEY>` seeds every page
  in a space. *Done when:* a page with a child tree indexes a bounded set, no
  loop, drop-count reported; `--space` bulk-indexes.
- **Step 3 — monitor + `rtfm confluence watch`.** Persist the crawled page set +
  each page's `version.number`; poll (batched by id via CQL/`content` search),
  re-index pages whose live version is higher. *Done when:* editing a monitored
  page is reflected within one poll cycle.
- **Step 4 — purge.** `rtfm confluence purge <id>|--all [--project]`, and fold
  Confluence config/monitor cleanup into the general `rtfm purge <project>`.
  *Done when:* a purged page vanishes from `search_docs` and stops being polled.

Zero MCP-tool changes: indexed pages are ordinary chunks.

*Step 1 delivered.* `Rtfm.Core/Confluence/` mirrors `Jira/`: `ConfluenceConfig`
(+ `ConfluenceConfigStore` under `LocalApplicationData/rtfm/confluence`, `${ENV}`
token default `CONFLUENCE_TOKEN`), `ConfluenceClient` (read-only REST v1, Basic
auth, GET-only — `FetchPageAsync` + a `/user/current` `VerifyAuthAsync`; handler
seam for tests), `ConfluenceModels` (+ `ConfluenceDate`), `ConfluenceSource`
(the `confluence://{id}` key + `ParsePageId` from a page URL or bare id), and
`ConfluenceDocumentRenderer` (`# <title>` + metadata blockquote + the
`body.view` HTML through the shared tail — the page's own `<h2>`/`<h3>` become
the chunk breadcrumbs, no synthetic structure). Ingest via the shared
`DocumentIngestor.IngestDocumentAsync`. CLI: `rtfm confluence config|index|list`.
Verified live against `internationalsos.atlassian.net`: `config` auth-checks
read-only; `confluence index <full page URL>` parsed the id and indexed the
AISDLC programme page → 5 chunks, `source_modified_at` = `version.when`
(2026-07-01), breadcrumb `AI SDLC … Programme > Description` (the body heading
drove it); search hit it #1 (1.00); **re-index by bare id stayed 5 chunks**
(the synthetic-key delete-by-query round-trips); the page's 4 children + 4
in-body links were detected (traversal fuel for step 2); `purge` removed all 5.
258 tests (13 new).

*Step 2 delivered.* `ConfluenceCrawler` + seed-aware `index`. The key finding:
**CQL `ancestor = {id}` flattens a page/folder subtree through sub-folders in
one query** (and `space = "{key}"` a whole space), so the page *tree* is
resolved in a single cheap call rather than level-walked. `ConfluenceSource.ParseSeed`
classifies the three URL shapes — `/pages/{id}` → page (+ its subtree via
`(id = X OR ancestor = X)`), `/folder/{id}` → folder (its subtree), `/spaces/{KEY}`
→ whole space — plus a bare id (page) and a `--space <KEY>` override. The crawl
resolves that scope, then follows **in-body `/pages/{id}/` links** breadth-first
to `--depth` (link hops beyond the scope; 0 = scope only), cycle-safe, non-page
content (folders/whiteboards) skipped. `--max-pages` budgets it and `--dry-run`
prints the scope plan. **Bug caught live and fixed:** scope was first resolved
capped at the runtime budget, so a 60-page folder with `--max-pages 8` indexed 8
and *silently* dropped 52 — violating §5. Now the full scope is listed (CQL is
id+title only, cheap), the whole set is enqueued, and the BFS budget-cap reports
the accurate `Dropped` count. Verified live on the real `PR` space: the
page-with-subpages resolved to 6 (self + 5 descendants), the folder to **60**
(pages nested through its sub-folders), the space capped at the budget; a real
`--depth 0` index of the page → 6 pages / 50 chunks with a descendant (`PAM
Database Schema Summary`) answering #1; the folder at `--max-pages 8` indexed 8
and reported "52 more discovered but not followed". 262 tests (4 new; a
malformed-JSON test-stub bug — unescaped `href="…"` quotes — was the only
failure, fixed with single-quoted fixture hrefs).

*Step 3 delivered.* The monitored-set registry + polling watch, on
`version.number` (Confluence's monotonic edit counter — cleaner than Jira's
timestamp, no timezone or precision worry). `ConfluenceMonitorStore` (per-project
`ConfluenceMonitor` of `MonitoredPage{id, lastVersion}` under
`LocalApplicationData/rtfm/confluence/monitor`); `confluence index` records its
crawled pages there. `rtfm confluence watch [--interval <s>] [--once]` polls:
`ConfluenceClient.FetchVersionsAsync` batches `id in (…)` CQL with
`expand=version` for each page's live number, `ConfluenceMonitor.SelectChanged`
(pure, unit-tested) returns the ids whose live version is higher than stored, and
each is re-fetched → re-rendered → `IngestDocumentAsync` → its version updated.
The monitor reloads each poll (picks up a concurrent index), `--interval` floors
at 30 s, and the first poll after a restart is the catch-up. Verified live: index
a 6-page subtree → 6 monitored; `watch --once` → "polled 6 — no changes";
backdating one page's stored version (simulating a remote edit without writing
to Confluence) → next poll re-indexed exactly that page ("PAM Platform
Assessment" → 11 chunks); the poll after → "no changes". 265 tests (3 new).

*Step 4 delivered (phase complete).* `rtfm confluence purge <PAGE-URL|id> | --all
[--project] [--yes]`: a single page deletes its chunks (scoped to `source_path`
+ `project`), drops its contradiction pairs, and removes it from the monitored
set; `--all` prefix-deletes every `confluence://` doc, drops the monitored
pages' pairs, and clears the monitor (confirms interactively, refuses without
`--yes` when redirected). The general `rtfm purge <project>` now also drops a
project's Confluence config + monitor (surfaced on the "what's on the block"
line), completing the Jira+Confluence symmetry. **Bug caught + fixed live:**
`purge --all` 409'd on the contradiction index — `RemoveForPathAsync` is called
per page in a loop, and a contradiction pair *between two monitored pages* is
matched by both deletes, so the second conflicts on the already-removed pair.
Fixed at the source in `OpenSearchGateway.DeleteByQueryAsync` — it now issues
`conflicts=proceed` (a delete-by-query snapshots versions; proceeding past a
conflict is the correct removal semantics and it fixes the same latent risk in
the Jira `purge --all` loop too). Verified live: index 6 pages → purge one (11
chunks, dropped from set, 6→5 monitored) → `purge --all` (39 chunks, monitor
cleared, `watch` then "nothing to watch") → `rtfm purge <project>` reports
"Confluence config + monitor removed". 265 tests (purge is I/O — validated live,
matching `PurgeCommand`). **Phase 26 done: Confluence — pages pulled by URL,
whole subtrees/spaces resolved via CQL, linked pages followed, watched on
`version.number`, and purged.** Version bump 1.6.0 → 1.7.0 (additive phase)
pending at release.

**Deliberately not planned:** web UI (the LLM client is the UX, §2.11), cloud
sync/hosting (per-dev local is the model, §intro). (Confluence API pull was
deferred through Phase 25; **Phase 26 delivers it** — §2.17, the same reversal
as Jira, once the parallel infrastructure already existed.)

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
