# PHASE 5 — Search & Investigation

## Context
Events are stored and the UI shell is secured. This phase makes the data usable. Search
is the feature operators touch most, so it sets the perceived quality of the whole product.

## Objective
A query language, a fast results grid, live tail, context view, and export — all
respecting Phase 4's scope filter.

## Build
1. **Query parser** supporting: free text (FTS5), `field:value`, `field:>value` and
   `field:<value`, quoted phrases, `AND` / `OR` / `NOT`, parentheses, and trailing
   wildcards. Compile to parameterized SQL — never string-concatenate user input.
2. Time range selector: quick presets (15m, 1h, 4h, 24h, 7d, 30d) plus absolute range.
   Time range is always applied; there is no unbounded query.
3. Results grid: virtualized scrolling, sortable, column chooser, saved column layouts
   per user, severity colour coding, expandable row showing all `event_fields` and the
   raw message.
4. **Context view** — from any event, open the ±N surrounding messages from the same
   host in a modal, N configurable, default 50. This is a headline differentiator; make
   it fast and make it one click.
5. **Live tail** — server-side push of new matching events with the active filter
   applied, pausable, with a buffer cap and a visible "N messages while paused" counter.
6. **Saved searches** — named, owned, optionally shared, with a one-click promote to
   dashboard widget (Phase 9) or alert definition (Phase 8). Store the query, not the results.
7. Export current result set to CSV, JSON, and raw syslog text. Exports are streamed,
   size-capped, and written to the audit log.
8. **Pattern tester page**: paste a sample message, select or write a GROK/regex
   pattern, see extracted fields live, save as an extractor. This closes the loop on Phase 3.
9. **Filter sidebar** — device, severity, stream, and time selectors that compose the
   query for the user. The query bar shows what the sidebar produced, so users learn the
   syntax by seeing it. **Typing a query must never be required to use search.** This is
   the single most important usability decision in the product; Graylog and Elastic both
   lose non-specialist users here.
10. Query bar autocomplete on field names and known values, with an inline syntax cheat
    sheet behind a `?` affordance.
11. Empty state on zero results explains *why* (no matches / no data in range / scope
    restriction) and offers to widen the time range with one click.

## Do not build in this phase
Dashboards, charts, alerting. Saved searches are stored but only consumed by search.

## Tests to write first
- Query-language matrix: at least 40 cases covering each operator, precedence,
  malformed input, and injection attempts. Assert malformed queries produce a clear
  user error, never an exception or a full-table scan.
- Scope test: a scoped user's search cannot return out-of-scope events via any operator.
- Context view test: correct neighbours returned, correct ordering, host isolation.
- Export test: streamed output matches the grid contents exactly.

## Verification — run these and paste output
```bash
dotnet test --filter "Search|Query|Export"
dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --filter "*Search*"
```
Assert the target: **< 2 s for a filtered query returning ≤ 1,000 rows over a 30-day
hot window** on a database seeded with 50 million events. Seed that database in the
benchmark; do not test against a toy dataset.

## UX gate (required — see `UX_STANDARDS.md`)
Run all five checks. Cold-eyes task: **find every authentication failure from one
specific switch in the last 24 hours, without typing a query.** Record the click count.
If it exceeds 5 clicks, redesign the sidebar before committing.

## Validation & Evidence (per `TESTING_STANDARDS.md`)

- **Golden oracle for query correctness** — implement a deliberately naive brute-force
  matcher over the same dataset, then assert the optimised SQL path returns **identical
  result sets** for 500 generated queries. This is the only reliable way to prove a query
  compiler is correct.
- **Injection suite** — SQL fragments, FTS5 syntax abuse, nested quotes, unicode
  normalization tricks, and 10KB query strings. Assert parameterization holds and every
  malformed query yields a user-facing error, never an exception or a full table scan.
- **Query plan assertions** — for the 10 most common query shapes, assert
  `EXPLAIN QUERY PLAN` uses the intended index. A query that silently degrades to a scan
  will pass a correctness test and fail a customer.
- **Latency percentiles, not averages** — p50/p95/p99 against the **50M-event** dataset,
  three runs. The p99 is what users actually complain about.
- **Pagination and virtualization** — scroll 50,000 rows, assert no duplicated or skipped
  rows and stable memory.
- **Evidence:** oracle divergence report (must be zero), query plan assertions, latency
  percentile table, UX gate with the no-typing search task click count.

## Security Validation (per `SECURITY_STANDARDS.md`)

- **Stored XSS through the full path** — ingest payloads from the OWASP XSS filter-evasion
  corpus, then assert correct encoding in the results grid, the expanded row, the context
  view, live tail, and JSON export. This is the product's highest-likelihood real
  vulnerability: attacker-controlled content rendered to an administrator.
- **CSV formula injection** — a message starting with `=`, `+`, `-`, `@`, tab, or CR must
  be neutralised **in the export only** (prefix per OWASP guidance), never in storage.
  Assert both halves: safe in the CSV, byte-identical in the database.
- **Query injection** — SQL fragments, FTS5 operator abuse, nested quotes, unicode
  normalization, 10KB queries, and stacked statements. Assert parameterization holds.
- **IDOR** — saved searches, column layouts, and exports accessed by another user's ID.
- **Scope bypass via query** — attempt to reach out-of-scope events through sort fields,
  wildcards, negation, and export parameters.
- **Export DoS** — request an export of 50M rows; assert streaming, a size cap, and that
  the UI stays responsive.
- **Evidence:** XSS matrix (payload × output surface), CSV injection proof, injection
  sweep, IDOR results.

## Definition of Done
Standard DoD, plus the search latency number against 50M rows recorded in PROGRESS.md,
and the no-typing search task completed in 5 clicks or fewer.

## Commit
`feat: phase 5 — query language, search grid, live tail, context view` → tag `v1.0.0-phase.5`
