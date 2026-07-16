# Rtfm.Mcp — the stdio MCP server

The `rtfm` MCP server Claude Code spawns via `.mcp.json`. Thin host: DI wiring
in `Program.cs`, tools as `[McpServerTool]` methods discovered by
`WithToolsFromAssembly()`. Search logic lives in `Rtfm.Core`.

## THE rule: stdout is the transport (§2.2)

stdout carries MCP JSON-RPC and *nothing else*. One stray write corrupts the
framing and the server silently fails to connect.

- **Never** `Console.WriteLine` / `Console.Out` here. Diagnostics go to
  stderr: the host logger is pinned with `LogToStandardErrorThreshold =
  LogLevel.Trace`, and ad-hoc messages use `Console.Error`.
- **Never** reference Spectre.Console or any console-UI package here.
- Core callbacks you wire (embedder logs, future watch events) must point at
  `Console.Error`.
- After any change to this rule's neighborhood, re-verify with the raw-stdio
  smoke check (initialize / tools-call over redirected pipes): stdout must
  parse line-by-line as JSON-RPC.

## Operational gotchas

- **Rebuild before reconnect.** `.mcp.json` points at the built Release DLL;
  a running server holds a file lock on its copied `Rtfm.Core.dll`, so
  full-solution builds fail while Claude Code has the server up (disconnect
  via `/mcp` or restart, then `dotnet build Rtfm.slnx -c Release`).
- Claude Code only reloads `.mcp.json` on restart; `/mcp` shows connection
  status and tools.
- Keep the MCP handshake instant: heavyweight services (the embedder) are
  registered lazily and load on first tool call, not at startup.
- Retrieval must never kill the server: `DocumentSearch` degrades to BM25 when
  embedding fails — preserve that property in new tools.

## Tool surface

- `search_docs(query, top_k, project?)` (`SearchDocsTool`) — hybrid retrieval;
  hits carry the short `source` filename, the full `path`, and the chunk
  `ordinal` (Phase 21) for chaining.
- `list_sources(project?, full?)`, `get_document(path, project?,
  around_ordinal?, radius?)`, `find_similar(path, top_k, project?)`
  (`CatalogTools`, Phase 8) — all backed by `DocumentCatalog` in Core; path
  arguments accept full paths or bare filenames (exact-then-wildcard
  resolution). Phase 21: unscoped `list_sources` across >1 project returns a
  per-project summary (`full=true` forces the dump); `get_document` with
  `around_ordinal` fetches just the chunks within `radius` of a hit and marks
  the result `partial`.
- `list_projects()` (`CatalogTools`, Phase 21) — project discovery via
  `StatusService` rollups (doc/chunk counts, recency, vector coverage).
- `ping()` (`PingTool`, Phase 21) — OpenSearch liveness inside a 5s cap, so
  an agent can check the stack before an expensive call instead of eating a
  client-side timeout. Never throws; returns `reachable` + an actionable
  error.
- `list_contradictions(project?, top_k)` (`ContradictionTools`, Phase 12) —
  nominated doc-vs-doc disagreements from `ContradictionDetector`; the
  description carries the read-both / prefer-newer / surface-the-conflict
  protocol (§2.13 B).
- `add_note` / `list_notes` / `remove_note` (`NoteTools`, Phase 13) — override
  notes backed by `NotesStore`. The `add_note`/`remove_note` descriptions
  enforce the human-in-the-loop precondition (explicit user confirmation in
  the conversation before calling) — keep that language intact; it is the
  §2.13 C safety model. Note ids are deterministic over (project, text,
  anchor) since Phase 21, so retried adds upsert; unanchored (pathless)
  notes are first-class for project-level decisions.
- `save_document(title, markdown, project?, author?)` (`DocumentTools`,
  Phase 19) — agent write-back via `GeneratedDocumentStore`: real file under
  `RTFM_GENERATED_DIR`, ingested through the full pipeline (this is why Mcp DI
  now carries `DocumentIngestor` + the contradiction detector). Same title =
  replace. The description's user-direction precondition is load-bearing.
- `list_databases(project?)` / `query_database(database, sql, max_rows?,
  project?)` (`DatabaseTools`, Phase 23) — the live-data gateway (§2.15). The
  only tools that bypass OpenSearch and read the actual database. Backed by
  `DatabaseRegistry` (static discovery) + `DatabaseQueryService` (DI; needs no
  gateway/embedder). The read guard lives in Core, not here — don't add a
  SQL-string filter, and don't let a descriptor without a `query` block become
  queryable. The descriptions' schema-first workflow (search_docs/get_document
  for the schema, *then* SQL) is what makes the tool produce correct SQL —
  keep it. `list_databases` advertises `writable`, and `query_database`'s
  description tells the agent to send writes only to a writable database and
  only when asked: reads are the default and a blocked write surfaces as an
  error, never a silent no-op.
- Scope for every tool resolves through
  `RtfmEnvironment.ResolveProjectScope` (`RTFM_PROJECT` default, per-call
  override, `*`/`all` sentinel).
- The `[Description]` on each tool is *agent-facing prompt text*, not a doc
  comment: it carries the recency/contradiction guidance (§2.13 B),
  cross-project attribution rules (§2.14), and cross-tool pointers (search →
  get_document/find_similar). When behavior changes, update the description in
  the same commit — a stale description misleads every agent that connects.
- Tool results are records serialized by the SDK; return raw data, no prose.
  Prefer explicit outcome fields (`found`, `vectorsAvailable`, `note`) over
  throwing — a tool error reads as "RTFM is broken", a structured miss reads
  as "adjust and retry".
