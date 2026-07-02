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

- `search_docs(query, top_k, project?)` — scope resolved by
  `RtfmEnvironment.ResolveProjectScope` (`RTFM_PROJECT` default, per-call
  override, `*`/`all` sentinel).
- The `[Description]` on each tool is *agent-facing prompt text*, not a doc
  comment: it carries the recency/contradiction guidance (§2.13 B) and
  cross-project attribution rules (§2.14). When behavior changes, update the
  description in the same commit — a stale description misleads every agent
  that connects.
- Tool results are records serialized by the SDK; return raw data, no prose.
- Phase 8 (`get_document`, `list_sources`, `find_similar`) lands here as new
  `[McpServerTool]` methods — same DI + description discipline.
