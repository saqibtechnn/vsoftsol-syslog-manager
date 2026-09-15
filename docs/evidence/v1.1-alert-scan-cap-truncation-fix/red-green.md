# v1.1 — live scheduler's window-scan-cap detection fixed — red/green

Per `TESTING_STANDARDS.md`: every new test observed failing before it passed.

## Background

Found while building the previous v1.1 item (P8-1, the alert preview full replay): the
identical off-by-one bug was independently present in the *live* scheduler's
`AlertEvaluationService.InMemoryAsync` (src/VSoftSol.Syslog.Service/Hosting/AlertEvaluationService.cs),
not just in the preview code being written at the time. Flagged as a separate follow-up
rather than fixed inline, since it is a different, already-tested production path. This item
is that follow-up.

## The bug

```csharp
await foreach (SyslogEvent evt in _reader.StreamWindowAsync(
    windowStart, now, deviceIds, alert.StreamIds, _options.MaxWindowScan, cancellationToken))
{
    if (++scanned > _options.MaxWindowScan)
    {
        truncated = true;
        break;
    }
    ...
}
```

`SqliteAlertWindowReader.StreamWindowAsync` issues `SELECT ... LIMIT $cap` with `cap` bound
to the same `_options.MaxWindowScan` value passed in. Since the SQL query itself already
caps the result set at exactly `MaxWindowScan` rows, `scanned` can never exceed
`MaxWindowScan` — the `truncated = true` branch was unreachable. A busy filtered alert with
more matching events in its window than `MaxWindowScan` (default 500,000) would silently
undercount, with `AlertWindowData.Truncated` never becoming `true`:

- `AlertEvaluationService.EvaluateOneAsync` never sent the operator-facing "hit its scan
  cap... the count is a lower bound" diagnostic notification.
- `ReconcileAsync`'s explicit "never auto-resolve when the scan was truncated" protection
  (added specifically so a truncated, possibly-still-breaching count can never be mistaken
  for a cleared condition) never actually engaged.

## Test

RED — `AlertEvaluationServiceTests.InMemoryScan_HittingItsCap_IsDetected_
AndRaisesTheScanCapNotification` (new), configuring a tiny `MaxWindowScan = 5` against 10
matching events for a filtered Threshold alert, then asserting the harness's recorded
notifications contain the "hit its scan cap" diagnostic:

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~InMemoryScan_HittingItsCap"
Failed!  - Failed: 1, Passed: 0, Skipped: 0, Total: 1
  Expected collection {empty} to have an item matching n.Title.Contains("hit its scan cap", Ordinal).
```

GREEN — after requesting `MaxWindowScan + 1` rows from `StreamWindowAsync` (mirroring the
identical fix already applied to `AlertAdminService.FullReplayAsync` in the previous v1.1
item) so that receiving the extra row unambiguously means there were more matching events
than the cap, rather than "there were exactly `MaxWindowScan` events":

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~AlertEvaluationServiceTests"
Passed!  - Failed: 0, Passed: 6, Skipped: 0, Total: 6, Duration: 6 s
```

All 5 pre-existing `AlertEvaluationServiceTests` cases (dedup, grouped firing, auto-resolve,
the 30-day time-travel firing-count test) still pass unchanged — the fix only changes
behavior at the scan-cap boundary, which none of them exercise.
