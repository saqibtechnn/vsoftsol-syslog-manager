# Phase 9 — verification

The exact commands from `phases/PHASE_09_DASHBOARDS.md` and `CLAUDE.md`'s Definition of Done,
with the real output.

## Build — warning-clean, 14 projects

```
$ dotnet build -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Unit tests

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release
Passed!  - Failed:     0, Passed:   807, Skipped:     0, Total:   807
```

## Integration tests

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release
Passed!  - Failed:     0, Passed:   585, Skipped:     0, Total:   585
```

(The pre-Phase-9 baseline was 1206 total; Phase 9 adds ~188 tests. No prior-phase test was
weakened — the only shared source change is `DatabaseSeeder` also upserting the four default
dashboards and `SqliteSavedSearchStore.GetQueryTextAsync`.)

## Phase filter

```
$ dotnet test --filter "Widget|Aggregation|Dashboard" -c Release
VSoftSol.Syslog.UnitTests        : Passed!  - Failed: 0, Passed: 95
VSoftSol.Syslog.IntegrationTests : Passed!  - Failed: 0, Passed: 97
```

## Style gate

```
$ dotnet format --verify-no-changes
exit 0 (after a `dotnet format` fix pass reindented the switch blocks in AggregationCompiler + 3 test files — generated-style indentation, no semantic change; same as Phase 8 slice E)
```

## Dashboard-load benchmark

```
$ dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --filter "*Dashboard*"
dashboard benchmark dataset: 2,000,000 events
| Method                                        | Mean          | P50           | P95           |
| cold load — 4 widgets, empty cache, 24h window | 6,154,640 us  | 6,133,069 us  | 6,200,078 us  |
| warm load — 4 widgets, cache hit               |       10.76 us|       12.05 us|       13.12 us|
```

Each measured iteration is 20 full dashboard loads (`InvocationCount=20`): cold load
**~308 ms p50 / ~310 ms p95 per 4-widget dashboard** over 2M events — under the 3 s target;
warm load (cache hit) sub-microsecond. The 50M-event acceptance → Phase 12 clean-VM run
(P9-1). Full output: `benchmark-run.csv`.

`DASHBOARD_BENCH_EVENTS` defaults to 2,000,000 (reusing the SearchBenchmark database when
present). The 50M-event, `< 3 s` p95 acceptance is carried to the Phase 12 clean-VM run
(P9-1) — the P1-1 / P5-1 / P6-1 pattern.

## SCA — no new dependency

```
$ dotnet list package --vulnerable --include-transitive
The given project `VSoftSol.Syslog.<each of 14>` has no vulnerable packages given the current sources.
```

The aggregation cache is a hand-rolled `ConcurrentDictionary` with wall-clock expiry;
`Microsoft.AspNetCore.Components.Web.HtmlRenderer` (visual-regression tests) is part of the
ASP.NET shared framework. `Directory.Packages.props` is unchanged.

## Coverage — ≥ 80 % on Ingestion / Rules / Reporting

See `coverage-summary.txt`.
