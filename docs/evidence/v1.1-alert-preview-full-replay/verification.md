# v1.1 — alert "would have fired" preview is a full replay, not a sample (P8-1 closed) — verification

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

## New/changed tests — targeted runs

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~AlertWebTests"
Passed!  - Failed: 0, Passed: 17, Skipped: 0, Total: 17, Duration: 685 ms

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~AlertSecurityTests"
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 61 ms
```

4 genuinely new tests in `AlertWebTests` (filtered Threshold, DistinctCount, Absence, capped
scan) plus the 13 pre-existing cases in that file, all green; `AlertSecurityTests`' 3 cases
unaffected by the constructor-signature change. See `red-green.md` for the two real
implementation bugs the tests themselves caught before this shipped (bucket-index precision,
scan-cap detection), and the two test-data bugs found and fixed alongside them.

## Unit tests — full suite, no regressions

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release
Passed!  - Failed: 0, Passed: 1072, Skipped: 0, Total: 1072, Duration: 1 m 10 s
```

Unchanged — this item is entirely integration-shaped (the preview always needed a real
SQLite-backed reader; no new pure logic worth a unit test in isolation).

## Integration tests — full suite, no regressions

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release
Passed!  - Failed: 0, Passed: 808, Skipped: 0, Total: 808, Duration: 7 m 51 s
```

804 (pre-existing) + 4 new. Fully clean this run — neither of the two previously-observed
load-sensitive dev-VM flakes reproduced.

## Security

No new attack surface. The preview already ran the operator-authored `ConditionGroup`
filter against `ScopedEventReader`/`SqliteAlertWindowReader`-scoped event data — the exact
same scope enforcement Phase 8 already proved; only the aggregation strategy sitting above
that already-safe query path changed (full bucket replay instead of sampled extrapolation).
No new dependency, no new query shape, no new privilege.

## UI verification

No new screen, route, or control — `AlertEditor.razor` already renders `AlertPreview.Detail`
verbatim (confirmed by reading the component: `<strong>@p.Detail</strong>`, no other
`Approximate`-driven copy anywhere in the page), so the updated sentence wording
("at least N times... the scan hit its row cap" instead of "approximately N times") reaches
the UI automatically. No separate browser walkthrough needed for a copy-only UI change
backed by fully-tested logic, the same disposition used for the P5-3/P10-2 items earlier in
this v1.1 series.

## Documentation updated

- `docs/RELEASE_NOTES.md` — new "Unreleased" bullet (v1.0.0's own text left untouched; P8-1
  was an internal `PROGRESS.md`/`known-issues.md` tracking item, never a user-documented
  v1.0.0 limitation, so there is no historical bullet to preserve here).
- `PROGRESS.md` — new v1.1 log entry, including the honest account of the two implementation
  bugs found and fixed via the tests themselves, and the separately-flagged sibling bug in
  the live scheduler; "Deferred items across all phases" table's P8-1 row struck through
  with **DONE (v1.1)**; "Current state" bullet updated to list all eight closed v1.1 items.
- `docs/evidence/phase-08/known-issues.md` — left untouched, per this project's "never
  rewrite history" convention (already confirmed via `git log` for every prior v1.1 item).

## Out-of-scope finding, flagged separately

While fixing the preview's own scan-cap detection, the identical off-by-one was found to
pre-exist, unfixed, in the live scheduler's `AlertEvaluationService.InMemoryAsync` (it also
requests exactly `MaxWindowScan` rows from a `LIMIT`-ed query, then checks
`scanned > MaxWindowScan` — a condition the `LIMIT` makes unreachable). This is a different,
already-tested production path outside this item's scope, so it was not fixed here — flagged
as a separate spawned task (`task_48e85434`) with full repro detail instead.

## Conclusion

P8-1 (`docs/evidence/phase-08/known-issues.md`) is closed: every alert type's "would have
fired" preview is now an exact full replay of the whole look-back (or an honest, clearly-
worded lower bound when the scan hits its cap), never a sampled estimate that could
disagree between two clicks of the same button. Zero regressions: unit 1072/1072,
integration 808/808, both fully clean on this run.
