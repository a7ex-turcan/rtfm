# Rtfm.Core — shared library

Everything that matters lives here; `Rtfm.Cli` and `Rtfm.Mcp` are thin hosts
over it. Root `CLAUDE.md` §2 holds the design decisions; this file is the
library-local rules.

## Hard rules

- **No host concerns.** Core never touches `Console`, Spectre, or MCP types.
  Anything a caller might want to show goes through a callback or a returned
  value — see `FolderWatcher`'s `Action<WatchEvent>` and `EmbeddingModelStore`'s
  `Action<string> log`. This is what keeps the MCP server's stdout pure (§2.2)
  without Core having to know MCP exists.
- **`PathNormalizer` is load-bearing** (§2.9/§2.12). The normalized path is the
  exact-match delete-by-query key *and* the bulk `_id` prefix. Any new code
  that stores, deletes, renames, or coalesces by path must round-trip through
  it — a missed normalization silently leaks stale chunks, it does not error.
  Keep the *original* path for file I/O (the key is lower-cased; it won't open
  on case-sensitive filesystems).
- **Embed what you index.** The text embedded into `content_vector` must be the
  same `Chunk.ContentWithBreadcrumb` that goes into `content` — lexical and
  semantic retrieval must see an identical document.
- **`WatchEvent.ToString()` is a pinned contract.** Redirected watch output is
  parsed by scripts; the format is locked by `WatchEventTests`. Add new event
  kinds rather than changing existing lines.
- **The read guard in `Database/` is a stray-write guard, not a security
  boundary** (§2.15) — say it that way, and don't oversell it. It lives at the
  *database*: `SET TRANSACTION READ ONLY` (Postgres) or a transaction that is
  always rolled back (SQL Server, which has no read-only mode but does have
  transactional DDL). Two invariants: a `.rtfmdb` without a `query` block stays
  un-queryable, and one without `allowWrites` stays read-only. **Rolling back is
  only half the guarantee — the undo must also be *reported* as an error.** Don't
  judge that by `RecordsAffected`: DDL reports `-1` there exactly like a SELECT,
  which is how a rolled-back `CREATE TABLE` once returned "OK — no rows returned"
  while the agent concluded it had written (fixed in
  `EvaluateSqlServerReadOutcome`: a read is confirmed by a *result set* coming
  back, never by rows-affected). Never "harden" it
  by pattern-matching the SQL string — CTEs, `SELECT INTO`, side-effecting
  functions, and stacked statements walk past that, so it reads as protection
  while being none. And never gate on login permissions: a local dev box is
  `sa`/superuser, so that refuses the only users the tool has (learned the hard
  way in Phase 23).
- **Raw JSON over the typed client** for OpenSearch (§2.10). Query/mapping
  bodies are serialized anonymous objects or raw strings in `RtfmIndex` /
  `DocumentSearch`; don't fight `OpenSearch.Net`'s typed surface. Watch the
  namespace clash: inside `Rtfm.Core.OpenSearch`, `OpenSearch.Net.X` needs the
  `OsHttpMethod`-style alias or `global::`.
- **OpenSearch is 2.19.x**: `hybrid` query + `normalization-processor`
  pipeline (min_max). RRF exists on this version but is deliberately unused —
  switching is a §2.10 decision, not a drive-by. History: 2.17's hybrid query
  500'd ("read past EOF … .nvd") on specific queries; `DocumentSearch` keeps a
  per-query lexical fallback for any future hybrid server failure.

## Map

| Namespace | Owns |
|---|---|
| `Configuration` | `RtfmEnvironment` — every env var (`RTFM_OPENSEARCH_URL`, `RTFM_PROJECT`, `RTFM_MODEL_DIR`) resolves here, nowhere else |
| `Conversion` | Format sniffing (`FormatDetector` — content, not extension) + per-format front ends sharing the strip→ReverseMarkdown tail |
| `Chunking` | `MarkdownChunker` — heading-aware, breadcrumbs, overlap, table-split-with-repeated-header |
| `Indexing` | `RtfmIndex` (mapping + analyzer + pipeline JSON), `DocumentIndexer` (delete-by-query + bulk), `DocumentIngestor` (the one convert→chunk→embed→index path both executables share), `PathNormalizer` |
| `Search` | `DocumentSearch` (hybrid with per-clause project filter; degrades to BM25 if embedding fails — never throw the search away), `DocumentCatalog` (list/get/similar reads for the Phase 8 MCP tools), `StatusService` (Phase 10 per-project rollups) |
| `Embeddings` | `LocalEmbedder` (lazy ONNX MiniLM) + `CrossEncoder` (Tier 3 reranker, max-window scoring — full chunks dilute MS MARCO scores), `EmbeddingModelStore` (per-model download/cache); `ITextEmbedder`/`IReranker` keep callers testable |
| `Contradictions` | `ContradictionDetector` — ingest-time kNN nomination of doc-vs-doc disagreements into the `rtfm-contradictions` side index (deterministic pair ids; *open* pairs dropped + re-evaluated when either doc re-ingests, dismissed/resolved ones are tombstones that survive and block re-nomination). Template suppression at two granularities (`content_hash` whole-chunk + `line_hashes` shared-line majority, both ≥3 docs). Nominations, never verdicts — humans close them (Phase 22: dismiss, or resolve into an override note) |
| `Notes` | `NotesStore` — override notes (§2.13 C): user-confirmed corrections in the `rtfm-notes` index, merged into retrieval as attributed `origin:"note"` hits + anchored annotations. Survive re-index by construction |
| `Database` | The **live data plane** (§2.15, Phase 23) — the one namespace that does *not* read the derived index: `DbDescriptor` (the `.rtfmdb` connector; env expansion is **lazy** so indexing and querying processes can hold different secrets), `DatabaseQueryService` (reads guarded *at the database* — Postgres `SET TRANSACTION READ ONLY`, SQL Server transaction+rollback; `allowWrites` opts a descriptor into writes), `DatabaseRegistry` (finds `*.rtfmdb` via manifest folders) |
| `Manifest` | Startup-reconcile state: normalized path → (mtime ticks, length), per-(folder, project) JSON |
| `Watch` | `FolderWatcher` (debounce/lock-retry/rename/reconcile) + `WatchEvent` |

## Conventions

- `InternalsVisibleTo("Rtfm.Core.Tests")` is set — expose pure logic as
  `internal static` for direct testing (`BuildBulkPayload`, `BuildHybridQuery`,
  `MeanPoolAndNormalize`) instead of testing through I/O.
- Async all the way down with `ConfigureAwait(false)`; `CancellationToken`
  last, defaulted.
- New packages: pin exact versions and record them in root `CLAUDE.md` §3.
