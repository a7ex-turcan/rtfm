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

## [1.3.0] - 2026-07-09

### Added
- `rtfm mcp-config --write` merges the `rtfm` server into an existing JSON MCP
  config **in place**, instead of only printing the snippet:
  - Idempotent — replaces the `rtfm` entry if present, adds it if not, and
    preserves every other server and top-level key in the file.
  - Backs the file up (`.bak`) before writing.
  - **Refuses to rewrite a file that contains comments (JSONC)** — it prints the
    snippet to paste instead, so hand-written comments are never lost.
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

[1.3.0]: https://github.com/a7ex-turcan/rtfm/releases/tag/v1.3.0
[1.2.0]: https://github.com/a7ex-turcan/rtfm/releases/tag/v1.2.0
[1.1.0]: https://github.com/a7ex-turcan/rtfm/releases/tag/v1.1.0
[1.0.0]: https://github.com/a7ex-turcan/rtfm/releases/tag/v1.0.0
