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

## Integration tests — full suite

A full-suite run was started before this commit but its output could not be captured in
this pass. The change is a single-line fix (`MaxWindowScan` → `MaxWindowScan + 1` in one
`StreamWindowAsync` call) in one method, already covered directly by the targeted
`AlertEvaluationServiceTests` run above (6/6 green, including every pre-existing scheduler
scenario: dedup, grouped firing, auto-resolve, the 30-day time-travel firing count) and
structurally identical to the same fix already fully verified end-to-end in the previous
v1.1 item (`AlertAdminService.FullReplayAsync`, `docs/evidence/v1.1-alert-preview-full-replay/`).
Every full-suite run so far this v1.1 series has been fully clean (see e.g. that same
item's 808/808). This gap is recorded here rather than papered over with an invented count;
if a subsequent full run surfaces anything, it will be logged as a new PROGRESS.md entry
rather than silently amending this one.

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
the first time since this code existed. Targeted and unit regression confirmed clean; the
full integration suite's confirmation for this specific run is pending (see above).
