# Phase 9 — dashboard-load benchmark

`DashboardBenchmark` (`tests/VSoftSol.Syslog.Benchmarks/DashboardBenchmark.cs`), 2,000,000
events (seeded into / reused from the SearchBenchmark database), a representative 4-widget
dashboard over a 24-hour window:

- widget 1 — `Count`, bucketed hourly, query `failed`
- widget 2 — `Count` group by `hostname`, top 10
- widget 3 — `Count` group by `severity` (the severity donut)
- widget 4 — `DistinctCount` of `source_ip`

`RunStrategy=Monitoring`, `InvocationCount=20`, `IterationCount=3`, `WarmupCount=1` — each
measured iteration is **20 full dashboard loads**.

```
BenchmarkDotNet v0.14.0, Windows 11 (VMware), .NET SDK 8.0.424
InvocationCount=20  IterationCount=3  RunStrategy=Monitoring  WarmupCount=1

| Method                                            | Mean          | P50           | P95           |
|-------------------------------------------------- |--------------:|--------------:|--------------:|
| cold load — 4 widgets, empty cache, 24h window    | 6,154,640 µs  | 6,133,069 µs  | 6,200,078 µs  |   (÷20 = 308 ms / 310 ms per load)
| warm load — 4 widgets, cache hit                  |       10.76 µs|       12.05 µs|       13.12 µs|   (÷20 ≈ 0.6 µs per load)
```

## Reading

- **Cold load (empty cache): ~308 ms p50 / ~310 ms p95 for one 4-widget dashboard over
  2M events** — comfortably under the 3 s target. The four aggregations run off the
  `received_utc` index; the 24 h window slice is ~50k events on this fixture.
- **Warm load (cache hit): sub-microsecond** — the scope-keyed cache returns the memoised
  `AggregationResult` objects; a shared wall dashboard on a 30 s refresh with N viewers
  issues one query per widget per 15 s TTL, not N.
- The `Mean` error is wide (13.65 % of Mean) — BenchmarkDotNet warns "executed on the
  virtual machine with VMware hypervisor". This is the same noise profile as P1-1 / P5-1 /
  P6-1.

## Carry — P9-1

The phase's literal acceptance is "dashboard load < 3 s against the **50M-event** database".
A 50M seed is a ~1.5 h operation on this 2-vCPU VM with noise-dominated percentiles.
Measured here at 2M (~310 ms p95); the 50M seed and the `< 3 s` p95 acceptance move to the
Phase 12 clean-VM acceptance run (P9-1), the established P1-1 / P5-1 / P6-1 pattern.

Full BenchmarkDotNet output: `benchmark-run.csv`.
