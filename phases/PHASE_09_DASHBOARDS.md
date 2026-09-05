# PHASE 9 — Dashboards

## Context
Search answers "what happened to this device". Dashboards answer "is anything wrong
right now". Every widget must be backed by a saved search plus an aggregation spec —
no special-case widget code, or this phase becomes unmaintainable.

## Objective
A generic widget framework and four shipped default dashboards.

## Build
1. **Widget contract**: `{ savedSearchId | inlineQuery, aggregation, visualization,
   timeRange?, refreshInterval? }`. One rendering path, one data path.
2. **Aggregation spec**: `count | distinctCount | sum | avg | min | max`, optional
   `groupBy` field, optional time bucketing with auto-selected bucket size.
3. **Visualization types**: time-series line/area, bar, single-value counter, top-N
   table, severity donut, message-rate gauge, recent-events table, device status grid.
4. **Dashboard**: named, owned, optionally shared, drag-to-arrange responsive grid,
   per-dashboard default time range, auto-refresh interval, full-screen mode for a
   wall display.
5. Widget time range inherits from the dashboard unless overridden.
6. **Default dashboards**, shipped and non-deletable but copyable:
   - *Network Overview* — message rate, top talkers, severity distribution, recent criticals
   - *Security Overview* — auth failures by host, config changes, distinct source IPs
   - *Device Health* — device status grid, silent devices, parse-failure rate by device
   - *Collector Health* — ingest rate, queue depth, spill size, DB size, disk free, drops
7. All widget queries respect the Phase 4 scope filter. A scoped user sees a dashboard
   with their data, not an error.
8. Aggregation results cached with a short TTL so a shared wall dashboard does not
   hammer the database.

## Do not build in this phase
Report generation or PDF export — that is Phase 10.

## Tests to write first
- Each aggregation type returns correct values against a seeded fixture dataset.
- Time bucketing test: correct bucket boundaries across DST and across midnight UTC.
- Scope test: two users with different scopes load the same shared dashboard and each
  sees only their own data.
- Empty-data test: every widget renders an empty state, never an exception.
- Cache test: TTL honoured, invalidated on time-range change.

## Verification — run these and paste output
```bash
dotnet test --filter "Widget|Aggregation|Dashboard"
dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --filter "*Dashboard*"
```
Assert the target: **dashboard load < 3 s** against the 50M-event database from Phase 5.

## UX gate (required — see `UX_STANDARDS.md`)
Run all five checks. Cold-eyes task: **add a widget showing the top 10 noisiest devices
to a new dashboard.** Widget creation must be a guided picker (pick a saved search → pick
a visualization → preview → add), never a JSON editor. Every widget renders a useful
empty state. The four default dashboards must be genuinely useful on day one with zero
customization — a user who never builds a dashboard should still get value.

## Validation & Evidence (per `TESTING_STANDARDS.md`)

- **Aggregation oracle** — every aggregation type verified against an independently
  written SQL query over the same fixture data. Sums, counts, and distinct counts are
  exactly the kind of thing that looks right and is wrong.
- **Time bucketing matrix** — bucket boundaries across DST forward and back, leap day,
  year boundary, and a device sending in a different timezone. Assert no double-counted
  or dropped buckets.
- **Visual regression** — snapshot each widget type against fixture data; assert
  pixel-stable rendering across runs so a styling change cannot silently break a chart.
- **Empty, sparse, and extreme data** — zero rows, one row, one series with 100k points,
  12 series, all-identical values, and negative/zero values. Every widget renders; none
  throws.
- **Concurrency** — 20 simultaneous dashboard loads against the 50M dataset; assert the
  cache holds and p95 load stays under 3 s.
- **Evidence:** oracle comparison, bucketing matrix, visual snapshots, concurrent load
  percentiles, UX gate click count.

## Security Validation (per `SECURITY_STANDARDS.md`)

- **Cross-scope leakage on shared dashboards** — the single highest risk here. Two users
  with different scopes load the same shared dashboard; assert each sees only their own
  data in every widget, including aggregate counts. **An aggregate that includes
  out-of-scope events is a data leak even when no individual event is displayed.**
- **Stored XSS** — widget titles, dashboard names, axis labels, and any log-derived value
  used as a category label (hostnames and app names come from the wire).
- **IDOR** — dashboards and widgets accessed and modified by ID across users.
- **Cache poisoning** — assert the aggregation cache is keyed by scope so one user's
  cached result can never be served to another.
- **Evidence:** cross-scope aggregate comparison, XSS results, cache key assertion.

## Definition of Done
Standard DoD, plus the dashboard load number recorded in PROGRESS.md and widget creation
completable in 5 clicks or fewer.

## Commit
`feat: phase 9 — widget framework, aggregations, default dashboards` → tag `v1.0.0-phase.9`
