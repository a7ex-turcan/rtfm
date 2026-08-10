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

## [1.12.0] - 2026-08-10

### Added
- **`rtfm jira index` and `rtfm confluence index` draw what they indexed as a
  tree** when they finish, using each source's own hierarchy — Jira's
  epic → story → subtask chain, Confluence's page ancestors — rather than crawl
  order, so the shape matches how the content is actually organised:

  ```
  18 ticket(s) indexed into myproject
  ├── CEM-231  Policy Management  3 chunks
  └── UNICORN-40  [ABAC] Policy Management  3 chunks
      ├── AEXP-144  [ABAC] Group Management  2 chunks   ← seed
      │   ├── AEXP-18  Group Creation & Lifecycle  7 chunks · 1 PR · 5 commits
      │   └── AEXP-21  Fetch eligible users  12 chunks · 5 PRs · 10 commits
      └── AEXP-149  [ABAC] Policy Assignment  3 chunks
  ```

  Mostly it's just nice to look at, but the shape earns its keep: a subtree that
  arrived only because a link pointed at it is immediately visible, which is how
  an unrelated project turning up at depth 2 stops being a surprise.

  The **seed is marked, not forced to the root** — traversal walks up as well as
  down, so a seed ticket's own parent epic is usually crawled too and genuinely
  sits above it. Roots reached by following links are grouped separately, but
  only when there are in-scope roots to contrast them with. Children sort
  naturally (`AEXP-19` before `AEXP-100`), the node count is capped at 80 with
  the remainder reported, and a parent cycle can't strand a node.

  Presentation only, on stderr, and drawn just on a live terminal — redirected
  output is byte-for-byte what it was before.

### Changed
- `JiraCrawlNode` now carries the ticket's `Development` panel, and
  `ConfluencePage` carries `AncestorIds` alongside the ancestor titles. Both
  were needed by the tree: the Development panel was fetched then discarded, and
  ancestor titles are unique only *within* a space, so a multi-space run would
  mis-join pages on them.

## [1.11.1] - 2026-08-07

### Fixed
- **One inaccessible Jira project no longer disables the Development panel for
  every other project in a crawl.** `View Development Tools` is a *per-project*
  permission, so the dev-status endpoint answers 403 for a project the account
  can't see — but 1.10.0 treated that like a withdrawn endpoint and latched the
  whole feature off for the rest of the run. A single cross-project issue link
  into such a project therefore stripped the Development panel from every ticket
  crawled after it, while the warning claimed the data was globally unavailable:

  > Jira development data is unavailable (HTTP 403 from the dev-status endpoint)

  Now only a 401 (bad credential) or 404/410 (endpoint withdrawn) latches — a
  403 warns once per project and the crawl carries on:

  > Jira development data for project CEM is not visible to this account
  > (HTTP 403 — the "View Development Tools" permission). Its tickets index
  > without the Development panel; other projects are unaffected.

  The rest of a forbidden project's tickets are still attempted rather than
  skipped: Jira also has issue-level security, so one 403 does not prove the
  project is uniformly closed, and a wasted summary request is cheaper than
  wrongly skipping a readable ticket.

  Whether an existing index actually lost anything depends on crawl order — only
  tickets pulled *after* the first 403 were affected. Re-index if a ticket you
  expect to carry pull requests or commits is missing them.

## [1.11.0] - 2026-08-07

### Added
- **`rtfm confluence index --space` is repeatable**, so several spaces index in
  one command instead of one run each:

  ```bash
  rtfm confluence index --space PR --space PH --project myproject
  ```

  It also combines with a page/folder/space URL or id, and duplicate keys
  collapse (`--space PR --space pr` is one space).

  Multiple seeds crawl as **one run**, not several — they share a visited-set
  and a budget. That matters beyond keystrokes: a page reachable from two
  scopes is fetched and counted once, and `--max-pages` stays a ceiling on the
  whole run rather than quietly becoming a per-space allowance (which is what
  running the command twice gives you). The `--dry-run` preview shows each
  seed's own count plus the union, because the per-seed numbers don't sum to
  the run when scopes overlap.

  Verified against two real spaces of 446 and 407 pages: 853 unique in-scope
  pages across the pair, `--max-pages 10` indexing 10 for the run rather than
  10 per space, and a bounded run reporting the 847 it did not follow.

## [1.10.2] - 2026-08-07

### Fixed
- **Confluence space/subtree indexing stopped at 100 pages, silently.** Any
  scope larger than one API page indexed only its first 100; the rest were
  dropped with no error, and re-indexing could not recover them.
  `/rest/api/content/search` **ignores** the `start` parameter, so offset paging
  returned the same first page every time — and because each page came back
  exactly `limit` long, the loop's "short page means done" exit never fired: it
  ran to the ceiling instead, re-reading the same 100 ids ~50 times while the
  crawler's visited set quietly absorbed the duplicates. Scope enumeration now
  follows the `_links.next` cursor. Measured on a real 407-page space: **100
  pages in ~50 requests → 407 pages in 5**.

  `rtfm confluence watch`'s version probe shared the same code path. Its
  client-side batching (≤100 ids per query) meant it never actually relied on
  the offset, but a short response would have dropped those pages' versions and
  left the watcher permanently blind to their edits — closed at the same time.

  Scope queries also gained an explicit `ORDER BY created asc`, so a
  `--max-pages`-truncated scope is a stable subset rather than an arbitrary one
  per run; and an enumeration that ends early (an HTTP failure partway through)
  now **reports** that the listing is incomplete instead of passing as
  complete — on `--dry-run` too, where an under-reported preview is worse than
  none.

  **Re-index required:** a corpus indexed from a space or subtree of more than
  100 pages is missing content, and re-indexing on an older build could not fix
  it. Re-run `rtfm confluence index` once on this version.

- **`rtfm jira index --follow-mentions` silently followed nothing.** The project
  lookup that validates `KEY-123` mentions passed `keys=true` to
  `/rest/api/3/project/search`, where `keys` is a *filter by project key*, not a
  "return only the keys" projection — so it filtered the search to a project
  literally keyed `true` and always came back empty. An empty key set makes
  mention-following a no-op, so the flag did nothing rather than failing
  visibly. On the workspace this was found against, the lookup goes from 0
  projects to 342.

## [1.10.1] - 2026-08-07

### Added
- `rtfm jira index` and `rtfm confluence index` set the terminal tab/window
  title while they run, so a long crawl shows its state from the tab alone when
  the window is minimised or buried. The crawl phase shows the latest event
  (`⏳ pulled AEXP-19 (depth 0, 1 PR(s), 3 commit(s))`) since no total exists
  until it finishes; the indexing phase, where the total is known, shows a bar
  and a ratio (`▰▰▰▱▱ 12/20 indexing AEXP-222`).

  This extends the same helper `rtfm watch` has used since 1.3.2 rather than
  adding a second mechanism, so the behaviour is identical: emitted as an OSC 0
  escape on stderr and **only** when stderr is an interactive terminal —
  redirected output is byte-for-byte unchanged — with the prior title restored
  on exit, including on Ctrl+C. Terminals Spectre reports as non-unicode get an
  ASCII bar. Titles are now capped at 80 characters, so a long Confluence page
  title can't push everything useful out of the tab.

## [1.10.0] - 2026-08-06

### Added
- **Jira tickets now index their Development panel** — the linked branches,
  pull requests, and commits that Jira shows beside a ticket but keeps off the
  issue resource. Pull requests carry title, state, source → target branch,
  repository, author, reviewers and who approved, and the URL; commits carry
  their **full message**. Each becomes its own chunk (`KEY: summary >
  Development > Pull requests` / `> Commits`), so "which PR implemented
  PROJ-123?" is directly answerable.

  The commit messages are the larger win: a well-written one carries the
  implementation rationale that exists in no wiki page and no ticket comment,
  and it is now retrievable alongside the docs. Validated on a real ticket — a
  question answerable only from a commit body ranks that commit's chunk first,
  with the next hit at zero.

  Pulled at every crawl depth, not just the seed, because pull requests hang
  off the *stories* under an epic. Both ingest routes carry it: `rtfm jira
  index` and `rtfm jira watch`. No CLI flags, no MCP tool changes, no mapping
  change.

### Notes
- This data comes from the endpoint behind Jira's own Development panel, which
  Atlassian does not publish as a supported API. RTFM therefore treats it as
  **best-effort and never fatal**: a failure warns once — the endpoint is
  latched off for the rest of the run rather than retried per ticket — and the
  tickets still index without the section.
- **Known limit:** opening or merging a pull request does not change the
  ticket's `updated` timestamp, which is what `rtfm jira watch` compares, so
  the watch loop does not notice pull-request activity on its own. The panel
  refreshes when the ticket itself changes, or on the next `rtfm jira index`.

## [1.9.0] - 2026-07-30

### Added
- **`rtfm describe project <name>`** — the detail view for a single project,
  laid out `kubectl describe` style: an aligned field block (documents, chunks,
  vector coverage, source-date span, last index time, note and contradiction
  counts) followed by sections for the breakdown **by source type** (`md`,
  `pdf`, `jira`, `confluence`, …), watched folders, Jira/Confluence connectors
  and their monitored sets, `.rtfmdb` connectors and their access level, the
  open contradictions, the override notes, and the document listing.

  Where `rtfm status` is one row per project across the machine, this is
  everything RTFM holds for *one* — the read-side mirror of what `rtfm purge`
  would delete. The name defaults to `RTFM_PROJECT`, so inside a wired-up repo
  the bare `rtfm describe project` works; an unknown name lists the projects
  that do exist. Documents are listed top-20 by chunk count with the remainder
  reported (`--all` lists every one), and documents sharing a filename are
  disambiguated by as much of their folder path as it takes.

  Everything reported is already indexed — no mapping, ingest, or MCP changes.

## [1.8.0] - 2026-07-27

### Added
- **Confluence page comments are now indexed** — both **footer** (general) and
  **inline** comments. Each comment becomes its own retrievable chunk under the
  page (like Jira ticket comments), and an inline comment carries the
  **highlighted passage it annotates**, so searching for that passage's topic
  surfaces the discussion. Comments are pulled on every `confluence index` and
  every `watch` re-index.

  Two limits worth knowing: `watch` polls a page's version number, and adding a
  comment does not bump it — so a comment-only change isn't detected until the
  page body next changes (re-run `index` to refresh on demand); and only
  top-level comments are indexed, not replies to comments.

## [1.7.0] - 2026-07-24

### Added
- **Confluence integration — pull wiki pages over the API and index them**
  (`rtfm confluence`, read-only; the client issues `GET` and nothing else).
  Mirrors the Jira integration, applied to a wiki of pages:
  - `rtfm confluence config --url <workspace> --email <you> [--token-env CONFLUENCE_TOKEN]`
    stores a per-project descriptor (URL + email + a `${ENV}` reference to the
    API token — the token itself lives only in the environment) and verifies
    auth read-only.
  - `rtfm confluence index <URL>` accepts a **page URL** (the page + its whole
    subtree), a **folder URL** (its subtree), a **space URL** or `--space <KEY>`
    (the whole space), or a bare page id. It resolves that scope via CQL
    (`ancestor`/`space`, which flattens sub-folders in one query) and then
    follows **in-body page links** breadth-first to `--depth`, bounded by a
    `--max-pages` budget (dropped pages reported), cycle-safe. Each page renders
    — headings and all — into heading-breadcrumbed chunks under
    `confluence://{id}`, carrying its version author and date. `--dry-run`
    previews the scope.
  - `rtfm confluence watch [--interval <s>] [--once]` polls the monitored page
    set and re-indexes any page whose `version.number` increased; the first poll
    after a restart catches up on anything changed while it was off.
  - `rtfm confluence purge <id> | --all` drops pages from the index and the
    monitored set. `rtfm purge <project>` now also clears a project's Confluence
    (and Jira) connector config and monitored set.

### Fixed
- **`purge --all` no longer fails with a version conflict.** Delete-by-query now
  runs with `conflicts=proceed`, so purging several documents whose contradiction
  pairs reference each other (the second delete hitting an already-removed pair)
  succeeds instead of 409-ing. Hardens both the Confluence and Jira `purge --all`
  paths.

## [1.6.0] - 2026-07-24

### Added
- **Jira integration — the first source RTFM pulls over an authenticated API**
  (`rtfm jira`, read-only by construction). Instead of manually exporting
  tickets, point RTFM at a Jira Cloud workspace and index by key:
  - `rtfm jira config --url <workspace> --email <you> [--token-env JIRA_TOKEN]`
    stores a per-project descriptor (URL + email + a `${ENV}` reference to the
    API token — the token itself lives only in the environment) and verifies
    auth with a read-only check.
  - `rtfm jira index <KEY>` pulls the ticket **and follows its links** — issue
    links, parent, subtasks, and epic children — breadth-first to `--depth`
    (default 2), bounded by a `--max-tickets` budget (dropped links are
    reported, never silent) with a visited-set guarding circular references.
    Each ticket is indexed as thread-granular chunks (description + one chunk
    per comment, breadcrumb `KEY: summary > Comment by author, date`) under the
    source key `jira://KEY`, carrying the ticket's real `updated` date and
    authors. `--follow-mentions` also chases `KEY-123` mentions in the seed's
    text (validated against real project keys); `--dry-run` previews the crawl
    plan without indexing.
  - `rtfm jira watch [--interval <s>] [--once]` polls the monitored set and
    re-indexes any ticket whose `updated` changed; the first poll after a
    restart catches up on anything changed while it was off.
  - `rtfm jira purge <KEY> | --all` drops a ticket (or all of them) from the
    index and the monitored set. `rtfm purge <project>` now also clears a
    project's Jira config and monitored set.
  - **Read-only, always:** the Jira client issues `GET` and nothing else —
    RTFM is a retrieval tool and never writes to a team's tracker.

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
