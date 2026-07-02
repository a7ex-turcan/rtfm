# Rtfm.Core.Tests

xunit suite for `Rtfm.Core`. Mirrors Core's namespace layout
(`Conversion/`, `Chunking/`, `Indexing/`, `Search/`, `Embeddings/`,
`Manifest/`, `Watch/`).

## Boundaries

- **Pure unit tests only**: no live OpenSearch, no network, no model
  downloads, no Docker. End-to-end behavior (real cluster, real corpus, MCP
  stdio) is validated by CLI smoke scripts at phase time, not here — don't
  try to fold that into the suite.
- The real corpus (`docs/`) is gitignored and confidential. Fixtures are
  small synthetic documents built inline or from checked-in strings; never
  copy real export content into a test.
- Anything filesystem-bound (e.g. `ManifestStore`) uses unique temp paths per
  test (`Path.GetTempPath()` + GUID) so parallel runs don't collide.

## Patterns

- Test Core's `internal static` seams directly (`InternalsVisibleTo` is set):
  `DocumentIndexer.BuildBulkPayload`, `DocumentSearch.BuildQuery` /
  `BuildHybridQuery`, `LocalEmbedder.MeanPoolAndNormalize`. Assert on parsed
  `JsonDocument` shapes, not string contains, when checking JSON.
- **Pinned-contract tests are intentional**: `WatchEventTests` locks the plain
  watch log lines that external scripts parse, and `IndexingTests` locks the
  index mapping fields. If one fails, the *code* regressed a contract — fix
  the code, don't update the expectation (unless the root CLAUDE.md phase
  notes say the contract moved).

## Running

- `dotnet test tests/Rtfm.Core.Tests/Rtfm.Core.Tests.csproj -c Release`
- Root gotcha applies: `dotnet test` rebuilds Core but **not** the CLI/MCP
  executables — rebuild the solution before hand-running a built DLL.
