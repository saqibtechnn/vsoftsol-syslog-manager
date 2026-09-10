# Phase 9 — concurrent dashboard load

The phase's Validation section: "20 simultaneous dashboard loads against the 50M dataset;
assert the cache holds and p95 load stays under 3 s."

## Integration (`DashboardConcurrencyTests`, `AggregationFixture` — 360 events)

| Test | Result |
|---|---|
| 20 concurrent loads of one grouped widget on the same key | the factory is invoked **once** (`computed == 1` — stampede protection); all 20 results agree and equal the full count (360); elapsed < 3 s |
| 20 concurrent loads of a 4-widget dashboard | every load returns the same per-widget point counts; elapsed < 3 s |

The cache collapses N concurrent callers on one `(scope, source, aggregation, range,
bucket)` key to a single query, so a shared wall dashboard on a 30-second refresh with M
viewers issues one query per widget per TTL, not M.

## Benchmark (`DashboardBenchmark`, 2,000,000 events, 24 h window, 4 widgets)

See `benchmarks.md` / `benchmarks.json`. Cold load = empty cache (4 aggregation queries),
warm load = cache hit. On the 2-vCPU VMware build VM the number reads noisy (BenchmarkDotNet
warns "executed on the virtual machine with VMware hypervisor"); the **50M-event, p95 < 3 s
acceptance is carried to the Phase 12 clean-VM run (P9-1)**, the P1-1 / P5-1 / P6-1 pattern.
