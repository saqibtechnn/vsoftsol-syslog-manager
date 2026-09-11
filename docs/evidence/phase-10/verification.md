# Phase 10 — verification

The exact commands from `phases/PHASE_10_RETENTION_REPORTS.md` and `CLAUDE.md`'s
Definition of Done, with the real output.

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
Passed!  - Failed:     0, Passed:   900, Skipped:     0, Total:   900
```

(Baseline before this phase: 807. Phase 10 adds 93 unit tests — Retention/Reports domain
types, compression, schedule, catalogue, validators.)

## Integration tests

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release
Passed!  - Failed:     0, Passed:   667, Skipped:     0, Total:   667
```

(Baseline before this phase: 585. Phase 10 adds the 81 tests under
`IntegrationTests/Retention/` — migration 008,
policy store, the tiering engine, archive tamper matrix, restore, retention security,
report content vs. an independent SQL oracle, report store, the full lifecycle, and
PDF/CSV rendering. One flaky test in this run recurred from a **pre-existing** condition
(`WalCrashConsistencyTests`, the documented P2-5 class, unrelated to this phase — confirmed
passing in isolation); a full clean re-run afterward showed **667/667 with zero
failures**, saved verbatim in `test-output.txt`.)

## Phase filter

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~Retention" -c Release
Passed!  - Failed:     0, Passed:    81, Skipped:     0, Total:    81
```

## Style gate

```
$ dotnet format --verify-no-changes
exit 0 (after a `dotnet format` fix pass reindented two test files' object initializers —
generated-style indentation only, no semantic change; re-verified by re-running the
affected test filter, still 81/81 — same pattern as Phase 8 slice E / Phase 9 slice E)
```

## Retention benchmark

See `benchmarks.md` — Hot→Warm compression throughput measured at 200k events; the literal
10M-backlog-with-concurrent-search acceptance is carried to Phase 12 (P10-1).

## SCA — two new dependencies, both clean

```
$ dotnet list package --vulnerable --include-transitive
The given project `VSoftSol.Syslog.<each of 14>` has no vulnerable packages given the current sources.
```

`ZstdSharp.Port` (pure-managed Zstd, MIT, no native binary — no platform risk) and
`QuestPDF` (Community licence — free under the small-business revenue threshold; the
licence is set once via `QuestPDF.Settings.License` in `ReportPdfRenderer`'s static
constructor). `Directory.Packages.props` pins both to exact versions, no floating ranges.

## Coverage — ≥ 80% on Ingestion / Rules / Reporting

See `coverage-summary.txt`. All three gated assemblies PASS; Ingestion and Rules are
byte-identical to Phase 9 (no code changed in either this phase).
