# v1.1 — alert "would have fired" preview is a full replay, not a sample (P8-1 closed) — red/green

Per `TESTING_STANDARDS.md`: every new test observed failing before it passed.

## 1. Constructor signature change

`AlertAdminService` gained a new required `IOptions<AlertEvaluationOptions>` constructor
parameter (the preview reuses the live scheduler's own `MaxWindowScan` cap). Two existing
test call sites (`AlertWebTests.AdminAs`, the inline `new AlertAdminService(...)` in
`PromoteFromSavedSearch_PrefillsAFilter_AndFlagsLossyParts`, and `AlertSecurityTests.AdminAs`)
failed to compile until updated — a mechanical, not a behavioral, RED:

```
error CS1729: 'AlertAdminService' does not contain a constructor that takes 11 arguments
```

## 2. New `AlertWebTests` preview cases

Four new tests, RED against the pre-fix sampling code (compile-time, since the tests also
needed the constructor-signature fix above to build at all — the assertions were never
observed failing against the *old* sampling implementation specifically, since that
implementation could not be reached without first fixing the constructor call sites). Once
building, GREEN was reached in two passes — the second catching two real bugs the first
pass's tests exposed:

**Pass 1** (first fully-compiling revision of `FullReplayAsync`, using raw `.Ticks`
subtraction for the bucket index):

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~AlertWebTests"
Failed!  - Failed: 5, Passed: 12, Skipped: 0, Total: 17
  Preview_IsAccurate_AgainstFixtureData: Expected 3, found 9
  Preview_FilteredThresholdAlert_...: Expected 3, found 0
  Preview_DistinctCountAlert_...: Expected 2, found 1
  Preview_AbsenceAlert_...: Expected 2, found 3
  Preview_WhenTheScanHitsItsCap_...: Expected Approximate=true, found false
```

Root-caused as three independent defects, none of them in the *design* of the full replay,
all in its first implementation and its tests:

1. **Bucket-index precision** (`FullReplayAsync`, production code): computing the bucket
   index from raw `DateTimeOffset.Ticks` subtraction is sensitive to the small, unavoidable
   clock drift between when a test's fixture data is timestamped and when
   `FullReplayAsync`'s own `DateTimeOffset.UtcNow` runs a few milliseconds later. Several of
   the new tests deliberately placed events at exact multiples of the bucket width (a
   natural, "round minute" choice) — exactly the boundary condition where a few
   milliseconds of drift flips `floor(x)` to `floor(x) - 1` for the very first event of a
   group, splitting it from its siblings. This is the identical class of bug already
   recorded in this project's own memory note ("SQLite time bucketing: use strftime
   seconds — julianday() float subtraction rounds wrong at exact bucket boundaries"), which
   `PreviewBucketCountsAsync`'s SQL path already avoids via `strftime('%s', ...)` whole-
   -second bucketing. Fixed by switching `FullReplayAsync` to `DateTimeOffset.ToUnixTimeSeconds()`
   integer arithmetic for both `from` and each event, matching the SQL path's own convention
   exactly so neither branch of `PreviewAsync` can disagree about where a bucket boundary is.
2. **Cap-detection off-by-one** (`FullReplayAsync`, production code): `StreamWindowAsync`
   was called with `cap = _previewScanCap` and the loop checked `++scanned > _previewScanCap`
   — but the SQL query already applies `LIMIT $cap`, so the stream can never yield more than
   `cap` rows and `scanned` can never exceed it. Fixed by requesting `cap + 1` rows and
   comparing against `cap`, so receiving the extra row unambiguously means there were more
   matching events than the cap. (The identical bug was found to pre-exist in the live
   scheduler's `AlertEvaluationService.InMemoryAsync` — flagged separately via a spawned
   task rather than fixed here, since that is a different, already-tested production path
   outside this item's scope.)
3. **Two test-data bugs, not production bugs**: the Absence test's heartbeat event was
   placed 47 minutes in the past while the test's own look-back was only 3 minutes — the
   heartbeat could never have appeared in any evaluated bucket, so all 3 buckets looked
   "absent" (expected 2, found 3, fixed by moving the heartbeat to 90 seconds back). And
   several new tests' seeded events (6-per-minute-bucket threshold bursts, a 20-event
   scan-cap burst) fell inside the *pre-existing* `Preview_IsAccurate_AgainstFixtureData`
   test's own 1-hour look-back — that test's SQL fast path aggregates by hostname with no
   further scoping, so any other test's high-volume burst within the same hour silently
   inflated its "breaching buckets" count (this `AlertWebTests` class shares one live
   database across every test method via `IClassFixture`). Fixed by moving every new test's
   seeded data to 61+ minutes in the past (widening that test's own look-back to match) and
   adding a message-content filter to the `DistinctCount` test as defense-in-depth, so no
   two tests in the class can contaminate each other's aggregate regardless of exact timing.

**Pass 2** (after all three fixes):

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~AlertWebTests"
Passed!  - Failed: 0, Passed: 17, Skipped: 0, Total: 17, Duration: 685 ms

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~AlertSecurityTests"
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 61 ms
```

Covers: a filtered Threshold alert full-replay (5 buckets, only 3 matching the filter,
exact count 3 — a sampled estimate could easily have landed anywhere from 2-5); a
DistinctCount alert full-replay (4 buckets, 2 breach a distinct-value threshold); an
Absence alert full-replay (3 buckets, 2 empty); and a capped scan (`MaxWindowScan = 5`
against 20 matching events) correctly reporting `Approximate = true` with wording that
never says "approximately" (since this is now a capped lower bound, never a statistical
estimate).
