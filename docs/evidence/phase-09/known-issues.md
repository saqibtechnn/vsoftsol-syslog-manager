# Phase 9 — known issues

## P9-1 — 50M-event dashboard-load acceptance → Phase 12 clean-VM run

**What:** the phase's acceptance target is "dashboard load < 3 s against the 50M-event
database from Phase 5". On the 2-vCPU VMware build VM a 50M seed is a ~1.5 h operation with
noise-dominated percentiles, and BenchmarkDotNet itself warns the measurement is on a
hypervisor (the same situation as the Phase 1 insert benchmark P1-1, the Phase 5 search
benchmark P5-1, and the Phase 6/7 ingest benchmarks P6-1).

**Here:** `DashboardBenchmark` seeds 2,000,000 events (reusing the SearchBenchmark database
when present) and measures a representative 4-widget dashboard load, cold (empty cache) and
warm (cache hit). `DashboardConcurrencyTests` proves 20 simultaneous loads are safe and the
cache collapses the work to one query per key. Every aggregation shape is index-backed off
`received_utc` (the Phase 5 `EXPLAIN QUERY PLAN` discipline; the aggregation compiler only
adds `GROUP BY` / `strftime` expressions on top of the scoped predicate).

**Disposition:** MARGINAL/carried, same as P1-1 / P5-1 / P6-1. The literal 50M seed and the
`< 3 s` p95 acceptance move to the Phase 12 clean-VM acceptance run. Operator to accept at
the `v1.0.0-phase.9` tag.

## Carried from earlier phases (unchanged)

- **P4-1 / P4-2** — DAST (OWASP ZAP) and axe-core / live keyboard-AT traversal / 1366×768
  screenshots; no browser on the build VM. The Phase 9 UX gate's points 4 and 5 were
  assessed against rendered HTML + source. → Phase 12 / CI.
- **P5-1** — 50M-event search benchmark. Sibling of P9-1. → Phase 12.
- **P3-3** — Stryker mutation testing (VsTest adapter does not deploy on the SDK-only
  host). The Phase 9 differential coverage is the aggregation oracle + the SQL-vs-hand-SQL
  comparison + the time-bucketing matrix. → CI host.

## Not deferred — resolved in this phase

- The `system_key` unique index was changed from partial to a plain `UNIQUE` column so
  SQLite accepts it as an `ON CONFLICT` upsert target (the seeder's idempotency). No
  behaviour change for user rows (NULL keys are distinct).
- `RateGauge` needle coordinates rounded to 1 dp for deterministic SVG output.
