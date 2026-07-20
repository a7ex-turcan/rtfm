# Changelog

All notable changes to RTFM are recorded here. The format is based on
[Keep a Changelog](https://keepachangelog.com/en/1.1.0/), and RTFM follows
[semantic versioning](https://semver.org/): patch for fixes, minor for additive
features, major for a breaking change to the CLI or MCP contract.

The version is declared once in `Directory.Build.props` and flows to every
assembly, the `rtfm` CLI banner, and the MCP server's advertised
`serverInfo.version`.

Each released version also appears as a
[GitHub Release](https://github.com/a7ex-turcan/rtfm/releases): pushing a
`vX.Y.Z` tag runs the release workflow, which publishes the NuGet packages and
mirrors the matching section below into the release notes.

## [1.5.1] - 2026-07-20

### Fixed
- **A threaded `.eml` no longer loses every message but the newest.** 1.5.0
  stripped quoted reply history from all email, on the assumption that chains
  are exported one message per file — so the quoted copy would be redundant with
  a sibling. That assumption was wrong for how exports are actually produced:
  Outlook's "Save as" writes the *whole thread* into a single `.eml`, so the
  strip deleted the thread and kept only the top reply. An 11-message thread
  indexed as one chunk containing a two-line reply, and adding a reply *shrank*
  the indexed content — backwards, and it silently hid answers that were in the
  file the whole time.

  Quote handling is now per container, which is the actual fix rather than a
  reversal:
  - **`.eml` is one MIME message**, so its quoted history is the only copy of
    the earlier thread in the file. It is split at the message boundaries and
    every segment is kept, oldest first, attributed by the sender and date of
    the inline header block that introduced it. Each message becomes its own
    breadcrumbed, retrievable chunk. Quoting in a threaded body is linear rather
    than repetitive — each message appears exactly once — so splitting
    duplicates nothing.
  - **`.mbox` holds every message separately**, so the quoted copy really is
    redundant with its siblings and stripping remains correct there. Without it
    a ten-message chain would index its first message ten times.

  This supersedes the "a lone `.eml` loses its history" limitation listed under
  1.5.0, which described the defect rather than a constraint.

  Real exports carry two Outlook separator dialects, sometimes in one file: a
  `________` divider with `From:`/`Sent:`, and a bare `From:`/`Date:` with no
  divider — and **no `>` prefixes at all**, so detection based on those alone
  found nothing. A bare `From:` counts as a boundary only when a `Sent:`/`Date:`
  follows within a few lines, so prose opening with "From:" does not split a
  message. Gmail's `On … wrote:` form is handled as well. Signature and
  disclaimer stripping is unchanged and now applies per message.

  `source_modified_at` still tracks the thread's newest date; per-message dates
  appear in the breadcrumb.

### Known limitations
- Exporting the same thread more than once as it grows (two filenames with
  overlapping messages) indexes the shared messages from both files. Content
  loss is strictly worse than duplication, so this trade is deliberate.
- Outlook's native `.msg` (a CFB container, not MIME) is still unsupported.
  Save or drag the message as `.eml`.

## [1.5.0] - 2026-07-20

### Added
- **Exported email chains are now indexable (`.eml`, `.mbox`).** Decisions get
  made in threads and never make it back into Confluence, and unlike every other
  input a message carries a real author and a real `Date:` header. A file becomes
  one document and each message its own section under the subject, so search hits
  arrive breadcrumbed as `subject > date > sender` and a question about a decision
  made mid-thread lands on that message rather than the whole chain. No new
  dependency — MimeKit already shipped for the Confluence MHTML route.

  Quoted reply history, signatures, legal disclaimers, and mobile footers are
  stripped before indexing. This is not cosmetic: a chain exported per message
  carries its first message quoted inside every later reply, so indexing raw
  bodies would store the same text once per reply and bury real answers under
  copies of the question.

### Changed
- **Format detection reads an 8 KB header window for email**, up from the 512
  bytes used for every other format. A real message's `Received:`/`DKIM-Signature`
  chain routinely pushes `Subject:` past 512 bytes — in the corpus this was
  developed against, byte 1540. The wider window applies only to the email rule,
  so a stray `<html` deep inside a CSV still loses to that file's extension.
- Email is detected ahead of MHTML and separated from it by the presence of
  recipients. MHTML is itself a MIME email container, so without the ordering a
  `.eml` would route through the Confluence converter and convert as a malformed
  page. Anything ambiguous still falls through to MHTML, unchanged.

### Known limitations
- Quote-stripping assumes a chain exported **per message**, one file each. A lone
  `.eml` holding only the final reply loses the earlier messages along with the
  quotes — use `.mbox` for a whole thread in one file. Reconstructing messages
  from quoted text is deliberately not attempted; it would reintroduce exactly
  the duplication the stripping exists to prevent.
- Outlook's native `.msg` (a CFB container, not MIME) is not supported. Save or
  drag the message as `.eml`.
- Contradiction detection does not reach email content. Measured against the
  0.75 nomination floor: message-vs-message similarity for a genuine
  `admin`/`super-admin` disagreement scores 0.7242, and message-vs-SQL-schema for
  a real, known disagreement scores 0.5267 — the latter correctly identifying the
  right table, so ranking holds while the absolute scale collapses. A single
  lower floor cannot serve both and would regress 1.3.x's nomination precision,
  so nothing was tuned. Retrieval is unaffected: `search_docs` answers these
  questions at full score.

## [1.4.1] - 2026-07-16

### Fixed
- **A rolled-back write on a read-only SQL Server database is now reported as an
  error, not a success.** 1.4.0 promised a write against a read-only `.rtfmdb`
  would be "reported as an error, never as a silent success". The rollback half
  worked — nothing ever persisted — but the reporting half didn't: `rtfm db query
  <db> "CREATE TABLE …"` printed `OK — no rows returned` and `query_database`
  returned `success: true`. An agent issuing DDL would conclude its write landed
  when it hadn't, which is worse than a plain failure: it proceeds on a false
  belief about the database.

  The cause was a bad signal. The read guard decided "was this a write?" from
  `RecordsAffected > 0`, but DDL reports `-1` there — exactly like a `SELECT`
  does — so `CREATE`/`DROP`/`ALTER` fell through to the success path. `INSERT`
  reports `1` and *was* caught correctly, which is why the guard looked right
  when it was first validated: the check only ever exercised DML.

  A read-mode statement is now treated as a confirmed read only if it came back
  **with a result set**; anything else is reported as rolled back, naming
  `allowWrites` as the way out. This over-reports the rare read-ish statement
  that returns nothing (a bare `PRINT`/`SET`), costing one retry, and
  under-reports nothing. RTFM still never inspects your SQL string.

  **Postgres was unaffected** — its `25006` comes from the engine as a real
  error, so it never had a reporting half to get wrong. Both the CLI (non-zero
  exit) and `query_database` (`success: false`) now surface the failure.

### Changed
- Nothing in the read guard's *behavior* changed: writes were rejected before
  this release and are rejected after it. If you relied on a read-only
  descriptor reporting success for a `CREATE`/`DROP` that never actually
  happened, that reply was the bug.

## [1.4.0] - 2026-07-16

### Added
- **Live database gateway** — RTFM already indexed your database *schema*
  (`.rtfmdb`, since 1.2); it can now read the *data*. Two new MCP tools bring
  the surface to fifteen:
  - `list_databases(project?)` — the `.rtfmdb` connectors found in your indexed
    folders, each with its provider and access level.
  - `query_database(database, sql, max_rows?, project?)` — runs SQL and returns
    the rows as a markdown table.

  The pairing is the point: an agent that can query a database but doesn't know
  its shape writes garbage SQL. RTFM has the schema indexed, so the agent looks
  the tables up first, *then* writes the query.
- `rtfm db list` / `rtfm db query <name> "<sql>"` — the same gateway from the
  console, for setup and dogfooding.
- **Opt-in per descriptor.** A `.rtfmdb` is queryable only if it carries a
  `query` block, which may set its own read-only `connectionString`, `maxRows`
  (default 500), and `timeoutSeconds`. Descriptors written before this release
  keep meaning exactly what they meant — schema pull, nothing more.
- **Reads by default, writes on request.** Add `"allowWrites": true` to the
  query block for a database the agent may modify (seeding a local test DB).
  Otherwise a write is rejected by Postgres (`25006`) or rolled back on SQL
  Server — and reported as an error, never as a silent success. The guard is a
  transaction, not a login check, so it holds even on a superuser connection.
  It stops an agent's stray write; it is not a security boundary, and RTFM does
  not filter your SQL for keywords (that would be false comfort).
- Results are capped and **truncation is detected, not assumed** — the reader
  fetches one row past the cap, so `truncated: true` is a fact and the agent
  knows to narrow its query rather than believing it saw the whole table.

### Changed
- `.rtfmdb` connection strings now expand `${ENV_VAR}` placeholders lazily, when
  a connection is opened, rather than when the descriptor is parsed. Indexing
  and querying happen in different processes, so each need only hold the secret
  it actually uses. No change to how descriptors are written.

## [1.3.2] - 2026-07-16

### Added
- `rtfm watch` sets the terminal tab/window title while it runs — an animated
  moon-phase icon plus the watch scope (`all`, the project name, or
  `N projects`) and a live indexed/removed/failed tally, so a backgrounded
  watcher shows its state from the tab alone. Emitted as an OSC 0 escape on
  stderr and only when stderr is an interactive terminal, so redirected output
  is unchanged; the prior title is restored on exit. Terminals Spectre reports
  as non-unicode get an ASCII spinner instead of the moon frames.

## [1.3.1] - 2026-07-09

### Fixed
- `rtfm watch --all` (and any multi-folder watch) no longer makes the terminal
  ring its bell continuously. The multi-folder live dashboard listed **one
  header row per watched folder** — an unbounded, tall block carrying
  ambiguous-width glyphs (`•`, `→`) that Spectre repainted in place every
  second; Windows Terminal answered each repaint with its bell. The header is
  now a single compact summary line (`watching N folders across M projects …`);
  per-folder attribution is unchanged (it still shows in the feed's **Source**
  column). Single-folder `watch` was never affected.

## [1.3.0] - 2026-07-09

### Added
- `rtfm mcp-config --write` merges the `rtfm` server into an existing JSON MCP
  config **in place**, instead of only printing the snippet:
  - Idempotent — replaces the `rtfm` entry if present, adds it if not, and
    preserves every other server and top-level key in the file.
  - Backs the file up (`.bak`) before writing.
  - **Refuses to rewrite a file that contains comments (JSONC)** — it prints the
    snippet to paste instead, so hand-written comments are never lost. `--force`
    overrides this (comments are dropped, but the `.bak` is kept).
  - Defaults the target to the project-local config for Claude Code
    (`.mcp.json`), Cursor (`.cursor/mcp.json`), and VS Code (`.vscode/mcp.json`);
    other clients take an explicit `--file <path>`. Continue (YAML) stays
    print-only.

## [1.2.0] - 2026-07-09

### Added
- `rtfm watch` now watches **multiple folders in a single process**:
  - `rtfm watch <folder...> --project <name>` — several folders under one project.
  - `rtfm watch --all [--project <name>]` — every previously indexed folder,
    resolved from the watch manifests (optionally filtered by project).
- All folders in one run **share a single embedding model (~100–200 MB) and one
  ingestor**, so watching N projects no longer means N processes each loading
  its own model. Ingest work is serialized across folders by a shared gate.
- Multi-folder live dashboard: a **Source** column attributes each event to its
  folder/project.
- **Support for other MCP clients.** RTFM's server has always been a standard
  stdio MCP server, so any MCP-capable agent (Cursor, VS Code Copilot agent
  mode, Windsurf, Cline, Continue, Zed, Claude Desktop, …) can use it. New:
  - `rtfm mcp-config --client <name>` prints a ready-to-paste config snippet in
    the right shape for each client (snippet → stdout, target file + caveats →
    stderr).
  - A "Wiring into other MCP clients" README section with per-client config
    files and shapes.

### Changed
- Watch manifests now persist the **original-cased folder path** so `--all` can
  re-open folders on case-sensitive filesystems. The normalized (lower-cased)
  path remains the manifest's identity key (§2.12). Existing manifests upgrade
  in place on the next save.

### Unchanged
- Single-folder `rtfm watch` behaves exactly as before, and the plain
  (redirected) event lines keep their pinned format, so watch smoke scripts
  continue to parse them.

## [1.1.0] - 2026-07-08

First release published to NuGet.

### Added
- Jira **"Export to Word"** support: these `.doc` files are *bare* HTML (unlike
  Confluence's MHTML), routed through a dedicated front end that recovers the
  `<title>` and the Jira `Updated:` byline as `source_modified_at`.
  `.html`/`.htm` files ride the same route.
- Packaging as **.NET global tools** (`dotnet tool install -g Rtfm.Cli` /
  `Rtfm.Mcp`) with a tag-driven CI publish pipeline to NuGet.

## [1.0.0] - 2026-07-07

Initial versioned release — the full tool, end to end.

### Added
- **Conversion** for Confluence MHTML (`.doc`), Word (`.docx`), Markdown, PDF
  (with OCR of embedded and standalone images), Excel (`.xlsx`), CSV, draw.io
  diagrams, and SQL schema files (`.sql`), plus live DB schema pull (`.rtfmdb`).
- **Heading-aware chunking** with breadcrumbs, overlap, and table-aware splits.
- **Hybrid retrieval**: smart BM25 over a technical-token analyzer, local
  in-process semantic embeddings (all-MiniLM-L6-v2), and a cross-encoder
  reranker (ms-marco-MiniLM) — all offline via ONNX Runtime.
- **Watch mode** with debounce, editor-lock retry, delete/rename handling, and
  startup reconcile against a per-(folder, project) manifest.
- **Per-project segregation** and a single shared OpenSearch index.
- **Knowledge recency & contradictions**: timestamped chunks, recency-aware
  retrieval, proactive doc-vs-doc contradiction detection with a dismiss/resolve
  lifecycle, and override notes that survive re-index.
- **MCP server** exposing `search_docs`, `get_document`, `list_sources`,
  `find_similar`, `list_projects`, `ping`, `list_contradictions`,
  `add_note`/`list_notes`/`remove_note`, `save_document`,
  `dismiss_contradiction`, and `resolve_contradiction`.
- A **Spectre.Console** CLI (`init`, `ping`, `index`, `search`, `watch`,
  `status`, `contradictions`, `note`, `purge`, `convert`, `chunk`) and a
  one-shot `rtfm init` machine bootstrap.
- Cross-platform CI across Windows, macOS, and Linux.

[1.3.2]: https://github.com/a7ex-turcan/rtfm/releases/tag/v1.3.2
[1.3.1]: https://github.com/a7ex-turcan/rtfm/releases/tag/v1.3.1
[1.3.0]: https://github.com/a7ex-turcan/rtfm/releases/tag/v1.3.0
[1.2.0]: https://github.com/a7ex-turcan/rtfm/releases/tag/v1.2.0
[1.1.0]: https://github.com/a7ex-turcan/rtfm/releases/tag/v1.1.0
[1.0.0]: https://github.com/a7ex-turcan/rtfm/releases/tag/v1.0.0
