# Rtfm.Cli — the `rtfm` console tool

Human-facing CLI (`ping` / `convert` / `chunk` / `index` / `search` / `watch`).
All real logic lives in `Rtfm.Core`; commands here parse args, wire Core
services, and present results.

## Stream contract (do not drift)

- **stdout = results** (search cards, help, ping report, `convert`/`chunk`
  markdown). **stderr = diagnostics** (index progress, watch dashboard,
  warnings). Exit codes: `0` ok, `1` failure, `2` usage error.
- Spectre output goes through `Ui.Out` (stdout) / `Ui.Err` (stderr) — never
  `AnsiConsole.*` directly, so the mapping stays auditable.
- **`Ui.Fancy` gates live rendering** (progress bars, the watch dashboard).
  When output is redirected, everything must stay plain, parseable text — the
  watch smoke scripts parse `WatchEvent.ToString()` lines from stderr, and
  that fallback is part of Phase 7's acceptance criteria.
- `convert` and `chunk` print raw markdown as their *result* — keep them
  Spectre-free on stdout forever (people pipe them).

## Spectre rules

- **Spectre.Console lives in this project only.** Never add it to `Rtfm.Core`
  (host-agnostic) or `Rtfm.Mcp` (stdout is the MCP transport, §2.2).
- Escape all dynamic text going into markup: `Ui.E(...)`, or use `Text` (which
  takes raw strings) instead of `Markup`.
- Accent color is `Ui.Accent` (orange). Status colors: green/yellow/red map to
  cluster health and event kinds; keep them consistent across commands.
- Start long non-render work (e.g. `EmbedderProvider.TryCreateAsync`'s one-time
  model download) *before* entering a `Progress`/`Live` block — interleaved
  plain writes corrupt live rendering.

## Conventions

- Dispatch is a plain `switch` in `Program.cs` (no System.CommandLine yet —
  adopt it only if the command surface grows past comfort; root §5 Phase 0).
- Shared arg parsing lives in `CommandArgs` (folder + `--project`); don't
  re-implement flag loops per command.
- Embedder acquisition goes through `EmbedderProvider.TryCreateAsync()`: on
  failure it warns on stderr and returns null, and every command must keep
  working lexical-only (§2.10 degradation rule).
- New commands: update `PrintUsage` in `Program.cs` *and* the README's usage
  block.
