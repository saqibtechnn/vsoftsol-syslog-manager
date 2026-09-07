# ADR 0013 — The search query language: AST in Core, SQL compilation in Data

**Status:** Accepted (Phase 5)

## Context

PHASE_05 builds a query language (free text, `field:value`, ordering operators, quoted
phrases, `AND`/`OR`/`NOT`, parentheses, trailing wildcards) that must compile to
parameterised SQL, respect the Phase 4 scope chokepoint, and be provably correct against a
golden oracle. Two constraints shape where the pieces live:

- **CLAUDE.md "Repository layout — do not invent alternatives"** — the projects are fixed.
  `Core` does no I/O; `Data` owns SQLite.
- **CLAUDE.md "Two seams only"** — `ILogRepository` and `IAuthenticationProvider`. "Do not
  add speculative interfaces beyond these."

## Decision

### 1. The parsed query (AST) and the reference evaluator live in `Core`.

`SearchQueryParser` → `QueryNode` tree, `SearchFields` (the field allow-list),
`FtsTokenizer`, and `QueryEvaluator` (the naive in-memory matcher) are pure, allocation-only
code with no I/O. They belong in `Core` next to `SyslogEvent`. `QueryEvaluator` doubles as
the **golden oracle**: the SQL path's result set must be identical to filtering the same
events with it (`SearchOracleTests` — 500 generated queries, 0 divergences).

`FtsTokenizer` reproduces SQLite's `unicode61 remove_diacritics 2 tokenchars '.:-_/@'`
tokenizer (SQLite folds with a built-in table, not ICU, and the runtime here is
`InvariantGlobalization`, so `string.Normalize` is a no-op — an explicit Latin fold table
is used).

### 2. SQL compilation lives in `Data` (`SearchCompiler`, `internal`).

`SearchCompiler` turns an AST + the sidebar's structured filters + the principal's
`UserScope` into a parameterised `WHERE` body and `ORDER BY`. Every user value is a bound
parameter; nothing user-supplied is concatenated into SQL text. The generated predicates
mirror `QueryEvaluator` exactly, including SQLite three-valued logic: `NOT` is compiled as
`NOT (IFNULL(x, 0))` so a NULL column under negation matches the boolean `!false` the
oracle computes (and it is the better UX — `NOT host:web01` includes events with no host).

### 3. No new seam. `ScopedEventReader` gains search methods; it is still the chokepoint.

`ScopedEventReader.SearchAsync` / `SearchStreamAsync` / `PollLiveAsync` parse, compile, and
execute against the connection factory directly (as `IsVisibleAsync` already does).
`ILogRepository` is **not** extended — a future PostgreSQL `ILogRepository` would bring its
own query compiler; the SQLite one is not part of the seam. `ScopeChokepointArchitectureTests`
still passes: no `Web` type takes an `ILogRepository` dependency.

`EventRowMapper` (internal, Data) is extracted so the search executor and
`SqliteLogRepository` project the canonical `events` schema identically.

### 4. Export formatters live in `Reporting`.

`SearchExportWriter` (CSV / JSON / raw text, streamed) is "report generation" — the
`Reporting` project's stated purpose. CSV formula-injection neutralisation
(`CsvFormulaGuard`) is applied **on export only**; the stored value is never altered
(Constraint 4). The streaming endpoint (`/search/export`) + audit write live in `Web`.

## Consequences

- The replaceable-strategy seam count is unchanged.
- `Core` gains no dependencies (the tokenizer and parser are hand-written; no regex on the
  lexer path, no `System.Text.Json`).
- `Reporting` stops being a Phase-0 shell.
- Saved searches, column layouts, and user extractors get tables in migration `003`;
  wiring user extractors into the ingest pipeline is Phase 6 (the configuration surface).
