# v1.1 — live scheduler's window-scan-cap detection fixed — verification

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
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~AlertEvaluationServiceTests"
Passed!  - Failed: 0, Passed: 6, Skipped: 0, Total: 6, Duration: 6 s
```

1 genuinely new test (`InMemoryScan_HittingItsCap_IsDetected_AndRaisesTheScanCapNotification`)
plus the 5 pre-existing cases in that file, all green. See `red-green.md` for the RED this
test produced against the pre-fix code (the notification queue came back empty against 10
matching events over a configured cap of 5).

## Unit tests — full suite, no regressions

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release
Passed!  - Failed: 0, Passed: 1072, Skipped: 0, Total: 1072, Duration: 1 m 22 s
```

Unchanged — this fix is entirely integration-shaped (the scheduler's own scan/notification
plumbing).

## Integration tests — full suite, no regressions

The full-suite run was in progress at commit time (its output hadn't landed after three
checks, so the commit went ahead on the targeted/unit evidence above rather than block
further — see the superseded note this replaces, kept honest at the time rather than
papered over). It has since completed:

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release
Passed!  - Failed: 0, Passed: 809, Skipped: 0, Total: 809, Duration: 16 m 19 s
```

808 (pre-existing) + 1 new. Fully clean — neither of the two previously-observed
load-sensitive dev-VM flakes reproduced.

## Security

No new attack surface — the fix corrects when an existing, already-reviewed diagnostic
notification fires and when an existing auto-resolve guard engages. No new query, no new
dependency, no privilege change.

## UI verification

Not applicable — no UI surface. The only observable change is that the pre-existing "hit its
scan cap" notification (already rendered wherever notifications already render) now
actually reaches the operator when it should, and the pre-existing "never auto-resolve on a
truncated scan" guard now actually engages.

## Documentation updated

- `docs/RELEASE_NOTES.md` — new "Unreleased" bullet (a real, if narrow, correctness/safety
  fix an operator running a busy filtered alert could be silently affected by).
- `PROGRESS.md` — new v1.1 log entry (not a numbered backlog item — a bug found and flagged
  while building the P8-1 preview item, fixed here as its own follow-up); "Current state"
  bullet updated to list this fix alongside the eight closed v1.1 backlog items.

## Conclusion

The live scheduler's `AlertEvaluationService.InMemoryAsync` can now actually detect when its
window scan hits `AlertEvaluationOptions.MaxWindowScan` — the operator-facing diagnostic
notification fires, and the "never auto-resolve on a truncated scan" guard engages, both for
the first time since this code existed. Zero regressions: unit 1072/1072, integration
809/809, both fully clean.
