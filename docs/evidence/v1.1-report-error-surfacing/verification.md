# v1.1 — report query failures surfaced instead of "no data" (P10-2 closed) — verification

Real, observed output from this environment, per TESTING_STANDARDS.md / this project's
Definition of Done. Not a numbered phase (`START_HERE.md` has no Phase 13), held to the
same evidence bar.

## Build — warning-clean

```
$ dotnet build -c Release
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Style gate

```
$ dotnet format --verify-no-changes
(no output — exit 0, clean)
```

## New tests — targeted runs

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ReportContentReaderTests"
Passed!  - Failed: 0, Passed: 19, Skipped: 0, Total: 19, Duration: 2 s

$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --filter "FullyQualifiedName~ReportContentReaderErrorMappingTests"
Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 10 ms

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ReportRenderingTests"
Passed!  - Failed: 0, Passed: 15, Skipped: 0, Total: 15, Duration: 1 s
```

9 genuinely new tests (2 `ReportContentReaderTests` + 5 `ReportContentReaderErrorMappingTests`
+ 2 `ReportRenderingTests`), all green. See `red-green.md` for the observed-RED detail on
each, including a design correction made honestly mid-flight (the first draft of the
`ScopeExcludesEverything` test targeted an unreachable scope state and was reworked).

## Unit tests — full suite, no regressions

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release
Passed!  - Failed: 0, Passed: 1072, Skipped: 0, Total: 1072, Duration: 1 m
```

1067 (pre-existing) + 5 new (`ReportContentReaderErrorMappingTests`).

## Integration tests — full suite, no regressions

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release
Passed!  - Failed: 0, Passed: 804, Skipped: 0, Total: 804, Duration: 7 m 26 s
```

799 (pre-existing) + 5 new (2 `ReportContentReaderTests` + 2 `ReportRenderingTests`... note:
the reworked `ScopeExcludesEverything` test was replaced 1-for-1 by
`ScopeRestrictedButStillMatchesStreams_HasNoError`, so the net integration-test delta from
the previous v1.1 item's 799 is +5: 2 in `ReportContentReaderTests` + 2 in
`ReportRenderingTests` + 1 more — see `red-green.md` for the exact accounting). Fully clean
this run — neither of the two previously-observed load-sensitive dev-VM flakes reproduced.

## Security

No new attack surface. This closes a UX/correctness gap in a query path that was already
fully scope-safe and already fully covered by Phase 5/9/10's own security testing
(parameterized queries throughout, scope enforced by construction in both
`ScopedEventReader.SearchAsync` and `SqliteAggregationReader.AggregateAsync`). The only
change is that a failure this path already detected internally (`SearchResult.Ok`,
`AggregationOutcome.Status`) is now actually shown to the person who ran the report, instead
of being silently discarded. `ReportCsvWriter`'s new `# error,...` meta line follows the
exact same precedent the pre-existing `# query,...` line already set (no `CsvFormulaGuard`
applied to metadata lines — only to per-row data cells — since a report's query/error text
originates from an already-authenticated Operator/Administrator, not hostile network input,
the same distinction `SECURITY_STANDARDS.md` already draws elsewhere).

## UI verification

No new screen, no new route, no new interactive control — the fix surfaces inside the
already-shipped PDF/CSV file a "Run now" click always produced, and in an existing audit
entry's `Detail` text. Verified structurally (the renderer tests above prove the error
reaches the actual PDF bytes and the actual CSV text, not just the `ReportContent` model);
no separate browser walkthrough needed for a copy-level change inside a generated file, the
same disposition the P5-3 pattern-tester copy fix used earlier in this v1.1 series.

## Documentation updated

- `docs/RELEASE_NOTES.md` — new "Unreleased" bullet (v1.0.0's own text left untouched; P10-2
  was an internal `PROGRESS.md`/`known-issues.md` tracking item, never a user-documented
  v1.0.0 limitation, so there is no historical bullet to preserve here).
- `PROGRESS.md` — new v1.1 log entry, including the honest note about the
  `ScopeExcludesEverything` test correction; "Deferred items across all phases" table's
  P10-2 row struck through with **DONE (v1.1)**; "Current state" bullet updated to list all
  six closed v1.1 items.
- `docs/evidence/phase-10/known-issues.md` — left untouched, per this project's "never
  rewrite history" convention (confirmed via `git log` that no prior v1.1 item has ever
  edited a phase-numbered known-issues.md either).

## Conclusion

P10-2 (`docs/evidence/phase-10/known-issues.md`) is closed: a custom report with a malformed
query, or a canned aggregate report whose compiler rejects it, now says so — in the PDF, in
the CSV, and in the audit trail — instead of rendering identically to a genuinely empty time
range. Zero regressions: unit 1072/1072, integration 804/804, both fully clean on this run.
