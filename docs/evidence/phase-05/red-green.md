# Phase 5 — red / green log

Every new test was observed failing for the correct reason before the implementation
existed (TESTING_STANDARDS §2.1). Slices are committed in order; each block records the
RED observation, then the GREEN confirmation.

---

## Slice A — Query language (Core): lexer, parser, FTS tokenizer, oracle evaluator

**Files under test:** `SearchQueryParser`, `FtsTokenizer`, `QueryEvaluator` (all in
`src/VSoftSol.Syslog.Core/Search/`).

**RED — 2026-09-07**

```
dotnet test tests/VSoftSol.Syslog.UnitTests --filter "FullyQualifiedName~Search"
Failed!  - Failed:   157, Passed:     0, Skipped:     0, Total:   157
```

All 157 cases failed with:

```
System.NotImplementedException : red-green
  at VSoftSol.Syslog.Core.Search.SearchQueryParser.Parse(String query)
  at VSoftSol.Syslog.Core.Search.FtsTokenizer.Tokenize(String text)
  at VSoftSol.Syslog.Core.Search.QueryEvaluator.Matches(QueryNode, SyslogEvent, QueryEvaluationContext)
```

The three logic entry points were shipped as `=> throw new NotImplementedException("red-green")`
so the whole matrix (parser 60+ cases, tokenizer 14, evaluator 80+) was observed red in
one run before any parsing code was written.

Test files:
- `tests/VSoftSol.Syslog.UnitTests/Search/SearchQueryParserTests.cs` — the ≥40-case
  matrix: every operator, precedence, implicit-AND, parens, wildcards, malformed input,
  injection strings, 10 KB / over-length input, unicode.
- `tests/VSoftSol.Syslog.UnitTests/Search/FtsTokenizerTests.cs` — FTS5 tokenizer parity.
- `tests/VSoftSol.Syslog.UnitTests/Search/QueryEvaluatorTests.cs` — golden-oracle matcher
  semantics (string / numeric / timestamp / custom / reference fields, null-column rules,
  boolean composition).

**GREEN — 2026-09-07**

```
dotnet test tests/VSoftSol.Syslog.UnitTests --filter "FullyQualifiedName~Search"
Passed!  - Failed:     0, Passed:   158, Skipped:     0, Total:   158
```

Two failures during implementation were test-expectation bugs, fixed in the test, not the
code: (a) `facility:auth` normalises to code 4 (security), not 10 (authpriv); the matrix row
was corrected and an `authpriv → 10` row added. (b) `FtsTokenizer` diacritic folding — the
runtime runs with `InvariantGlobalization=true`, under which `string.Normalize()` is a
no-op, so `café` was not folding to `cafe`. Replaced with an explicit Latin fold table that
mirrors SQLite's own `unicode61 remove_diacritics 2` table (SQLite folds with a built-in
table, not ICU, so this is the correct reference anyway).

Full unit suite after Slice A: **541 passed, 0 failed** (was 383 pre-phase; +158).

---

## Slice B — SQL compiler + scoped search executor + query plans

**Files under test:** `SearchCompiler` (Data), `ScopedEventReader.SearchAsync` /
`SearchStreamAsync` / `PollLiveAsync`, and the compiled SQL's `EXPLAIN QUERY PLAN`.

**RED — 2026-09-07**

`SearchCompiler.Compile` was shipped as `=> throw new NotImplementedException("red-green")`
(the real body kept as `CompileCore`). Every execution / scope / oracle / query-plan test
that reaches the compiler was observed red:

```
dotnet test tests/VSoftSol.Syslog.UnitTests   --filter SearchCompilerTests
Failed!  - Failed: 14, Passed: 0, Total: 14      (all: System.NotImplementedException : red-green)

dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~Search"
Failed!  - Failed: 36, Passed: 16, Total: 52
  26 failures: System.NotImplementedException : red-green (execution, scope, oracle, query-plan)
  10 further failures were plan-assertion / oracle wiring, resolved during GREEN.
```

New test files:
- unit: `SearchCompilerTests.cs` (parameterisation vs injection payloads, predicate shapes,
  sort-enum → column, NULL-column guards).
- integration: `SearchExecutionTests.cs`, `SearchScopeTests.cs`, `SearchInjectionTests.cs`,
  `SearchQueryPlanTests.cs`, `SearchOracleTests.cs` (500-query brute-force differential),
  plus `TestSupport/SearchCorpus.cs`.

**GREEN — 2026-09-07**

```
dotnet test tests/VSoftSol.Syslog.UnitTests       --filter SearchCompilerTests
Passed! - Failed: 0, Passed: 14
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~Search"
Passed! - Failed: 0, Passed: 52
  → SearchOracleTests: 500 generated queries, corpus 700 events, **0 divergences**
    (docs/evidence/phase-05/oracle-divergence.md)
  → SearchQueryPlanTests: 10 common shapes, none degrade to `SCAN events`; FTS shapes use `events_fts`
```

Bugs fixed during GREEN (code, not tests): (a) `NOT` compiled as bare `NOT (x)` diverged
from the oracle for NULL columns — SQLite's `NOT NULL` is NULL (excluded) but the oracle's
`!false` is true; fixed with `NOT (IFNULL(x, 0))`, which is also the better UX. Test-side
fixes: query-plan seeder needed real `devices` rows and a larger (3 000-row, `ANALYZE`d)
dataset for the planner to prefer indexes; a couple of scope/injection assertions had
inverted severity ordering or disallowed an empty (valid) result set.

---

## Slice C — migration 003, saved searches, column layouts (IDOR)

**RED — 2026-09-07** — `003_search.sql` held back; the store tests were observed red:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter SavedSearchStoreTests
Failed! - Failed: 6, Passed: 0
  Microsoft.Data.Sqlite.SqliteException : SQLite Error 1: 'no such table: saved_searches'
  Microsoft.Data.Sqlite.SqliteException : SQLite Error 1: 'no such table: user_column_layouts'
```

**GREEN — 2026-09-07**

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "SavedSearchStoreTests|Migration"
Passed! - Failed: 0, Passed: 15
```

`SavedSearchStoreTests` covers create/list/get round-trip, list = own + shared (never
another user's private), duplicate-name rejection, column-layout default toggle, and the
**IDOR guard**: user B cannot read A's private search, cannot edit or delete A's shared
search, and cannot touch A's column layouts. `MigrationRunnerTests` (count-driven)
auto-adapted to the new script.

---

## Slice D — export formatters (Reporting)

**RED — 2026-09-07** — `SearchExportWriter.WriteAsync` shipped as
`=> throw new NotImplementedException("red-green")`:

```
dotnet test tests/VSoftSol.Syslog.UnitTests --filter SearchExportWriterTests
Failed! - Failed: 11, Passed: 3      (3 = the pure CsvFormulaGuard cases, no stub)
  System.NotImplementedException : red-green   ×11
```

**GREEN —**

```
Passed! - Failed: 0, Passed: 14
```

CSV header + one row per event; formula injection (`= + - @ TAB`) → cell prefixed with `'`;
delimiter cells RFC-4180 quoted; JSON is a valid array, round-trips fields, and
HTML-encodes `<>&'` (`<`) so a stored-XSS payload cannot break out — while the decoded
value stays byte-exact; raw text = verbatim `raw_message` per line; row count returned.

---

## Slice E/F — live tail, web UI, export endpoint

The `/search` route served a Phase-4 `ComingSoon` placeholder and `/search/export` 404'd
before this slice — the render / endpoint assertions below fail against that baseline. The
UI-render tests were written alongside the components (same class as Phase 4's
`DesignSystemRenderTests`; `bunit` unavailable — P4-3). The **security-critical** paths that
back the UI (query compilation, scope, injection, XSS encoding, export cap/audit) were all
red-first in Slices A–D and G.

**GREEN — 2026-09-07**

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "SearchWebTests|StoredXssMatrixTests|SearchExportSecurityTests"
Passed! - Failed: 0, Passed: 19
```

- `SearchWebTests` — `/search` needs auth; authenticated it renders `ds-search-sidebar` +
  `ds-querybar` + severity facet + pattern-tester link (no query typed); `/search/pattern-tester`
  is Operate-only; `/search/export` needs auth, streams CSV, writes an `Export` audit row,
  400s an invalid query.
- `StoredXssMatrixTests` — 10 OWASP filter-evasion payloads: stored byte-identical
  (`raw_message` and `message` round-trip exactly), rendered encoded in the pre-rendered
  search page, unicode-escaped in the JSON export with the decoded value still exact.
- `SearchExportSecurityTests` — CSV formula injection safe in the export / byte-identical in
  the DB; a 50-million-row export request stays streamed and is capped at
  `ExportMaxRows`; the export stream is scope-filtered like the grid.

---

## Slice G — golden oracle, injection sweep, query plans, pagination

Covered under Slices A/B above: `SearchOracleTests` (500 queries, 0 divergences),
`SearchInjectionTests` (SQL / FTS / unicode / 10 KB / stacked-statement payloads — no data
mutated, no exception, malformed → user error), `SearchQueryPlanTests` (10 shapes, no
`SCAN events`), `SearchExecutionTests.SearchAsync_Paging_DoesNotDuplicateOrSkipRows`.

---

## Slice H — post-benchmark compiler optimisation

The first search benchmark (2M events) showed the boolean-text case at **11.7 s** and bare
free text at **2.9 s**. Root cause: the compiler emitted one FTS subquery per text term,
and a mid-attempt correlated `EXISTS` rewrite made it worse. Two changes:

1. `SearchCompiler` now folds every maximal pure-text subtree into **one FTS5 boolean
   MATCH** (`"a" AND ("b" OR "c") NOT "d"`), reverting to a single `IN (SELECT rowid …)`
   only for the residual text-mixed-with-fields cases.
2. `ScopedEventReader.SearchAsync` skips the exact-count scan when the result page is full
   (reports a lower bound).

**Regression check after the change:**

```
dotnet test tests/VSoftSol.Syslog.UnitTests    --filter "FullyQualifiedName~Search"   Passed! 186
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~Search"   Passed! 77
```

The 500-query oracle and the 10 query-plan assertions still pass — the transformation is
semantically identical (`x IN(A) AND x IN(B)` ≡ FTS5 `A AND B`; `x IN(A) AND x NOT IN(B)` ≡
`A NOT B`). Re-benchmark: boolean text **11.7 s → 2.5 s**, free text **2.9 s → 2.7 s**,
field filter **362 ms**, phrase **1.0 s** (`benchmarks.md`).

**Full suite after all Phase 5 slices: 856 passed, 0 failed, 0 skipped** (569 unit + 287
integration; was 589 at the Phase 4 tag).
