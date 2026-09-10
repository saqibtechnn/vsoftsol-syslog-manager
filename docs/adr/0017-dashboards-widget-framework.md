# ADR 0017 — A generic widget framework, aggregation over the scoped search predicate, and a scope-keyed result cache

**Status:** Accepted (Phase 9)

## Context

Search answers "what happened to this device". Dashboards answer "is anything wrong right
now". The phase prompt is emphatic that **every widget must be backed by a saved search
plus an aggregation spec — no special-case widget code, or this phase becomes
unmaintainable** — and that a widget aggregate that counts out-of-scope events is a data
leak even when no row is shown.

Three decisions follow from that.

## Decision 1 — one widget contract, one data path, one render path

A widget is `{ source, aggregation, visualization }`:

- `source` — `EventQuery { savedSearchId? | inlineQuery }` **or** `SystemSeries { metric }`.
- `aggregation` — `{ function: count|distinctCount|sum|avg|min|max, valueField?, groupByField?, bucket, topN }`.
- `visualization` — line / area / bar / counter / top-N table / severity donut / rate
  gauge / recent-events table / device-status grid.

`WidgetDataService.LoadAsync` is the single entry point. Aggregation widgets flow through
`SqliteAggregationReader`; the two inherently row-shaped visualizations (recent events,
device grid) are the only branches that read rows instead of numbers, and each is one
method, not per-widget code. The nine visualization components are pure (parameters only) —
`WidgetCard` is the sole component that touches DI.

## Decision 2 — aggregation is compiled on top of the Phase 5 scoped search predicate

`SqliteAggregationReader` parses the widget's query with `SearchQueryParser`, compiles it
with the Phase 5 `SearchCompiler` (which bakes the principal's `UserScope` stream/device
clauses into the `WHERE`), then `AggregationCompiler` appends a `SELECT <agg>[, bucket][,
group] … GROUP BY` to that predicate. **The scope is part of the `WHERE` by construction** —
there is no code path that aggregates events the viewer cannot see. Two viewers of one
shared dashboard get different data, aggregates included. Group-by and value columns resolve
against a fixed allow-list (`EventColumns`, parity-tested against the Phase 8 alert window
reader); extracted-field names and the time-bucket origin are bound parameters. Time
bucketing is integer `strftime('%s')` seconds from a fixed origin (never `julianday` floats
— the Phase 8 note), so a bucket that straddles a DST change or midnight is still uniform
width.

**Alternative rejected:** a bespoke aggregation query builder. It would have re-implemented
— and could drift from — the scope clauses that `SearchCompiler` already gets right, which
is exactly the leak the phase warns about.

## Decision 3 — a short-TTL, scope-fingerprinted result cache

`AggregationCache` memoises widget results for `Dashboards:CacheTtl` (15 s default) so a
shared wall dashboard on a 30-second refresh does not re-scan the database for every viewer.
The cache key is `ScopeFingerprint(scope) | source | aggregation | fromUtc | toUtc | bucket`
— **the scope fingerprint is part of the key**, so one viewer's cached aggregate can never
be served to another (cache poisoning / cross-scope leak). A time-range change changes the
key, which is the invalidation. Entries compute once under a burst (stampede protection)
and a failed factory is not cached.

**Alternative rejected:** `Microsoft.Extensions.Caching.Memory`. Phases 7 and 8 shipped no
new dependency; a 40-line `ConcurrentDictionary` with wall-clock expiry is sufficient for a
single node (ADR 0005).

## Decision 4 — Collector Health reads a sample table, through the same path

The Collector Health dashboard needs ingest rate, queue depth, spill size, DB size, disk
free, and drops — none of which is an event query. A `CollectorStatSampler` background
service (collector host only) writes a `collector_stat_samples` row every 30 s from
`IngestionStatistics` + the DB file size + the data-drive free space, pruned to 72 h.
`SystemSeriesReader` turns one metric column into the same `AggregationResult` shape the
event path produces, so the visualization components are unchanged. When the Web host runs
standalone (no collector), the table is empty and the widgets show their empty state.
This keeps "no special-case widget code" honest: one additional typed source, not bespoke
per-widget logic.

## Decision 5 — dashboards persist as a whole; defaults are seeded and copyable

The `dashboards` row carries `widgets_json` + `layout_json` (the `actions_json`-on-rules
shape). A dashboard is validated (`DashboardValidator`, the same check the picker enforces)
and saved atomically. The four shipped dashboards are `is_system` rows seeded by
`DatabaseSeeder`, keyed by `system_key` for idempotent re-seeding; they are non-editable and
non-deletable, and "Copy to edit" (available to every authenticated user) produces an owned
clone.

## Cost accepted

- The extracted-field group-by / value path uses a correlated `event_fields` subquery per
  row; acceptable within a bounded time window, measured by `DashboardBenchmark`, carried
  to the Phase 12 clean-VM run at 50M events (P9-1).
- The cache is per-process (ADR 0005); a future multi-node story would need a shared cache
  or per-node acceptance of a 15 s divergence.
