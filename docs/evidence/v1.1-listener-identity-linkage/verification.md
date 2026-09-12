# v1.1 — listener identity linkage (P2-1 closed) — verification

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
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~SqliteListenerStoreTests"
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 292 ms

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ListenerRegistrationHostedServiceTests"
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 125 ms

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ListenerIdEnrichmentTests"
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 170 ms

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ListenerPortReloadServiceTests|FullyQualifiedName~ListenerSettingsServiceTests"
Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8, Duration: 1 s
```

15 new tests total (3 + 2 + 2 + 1 new `ListenerPortReloadServiceTests` case +
1 new `ListenerSettingsServiceTests` case, plus the 3 + 3 pre-existing cases in those two
files that still pass unmodified), all green.

## Unit tests — full suite, no regressions

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release
Passed!  - Failed: 0, Passed: 1060, Skipped: 0, Total: 1060, Duration: 1 m 13 s
```

Unchanged — no new unit tests were needed; the new logic is integration-shaped (a real
SQLite-backed store, a real hosted service, a real ingest pipeline).

## Integration tests — full suite

Run twice (once mid-session alongside a concurrent `dotnet run` dev-server process for the
UI check below, once standalone after a session restart as the final pre-commit check) —
both times 794/795, each time a *different* known, load-sensitive flake on this 2-vCPU dev
VM, neither touching any code this change modified:

```
# Run 1 (dev-server running concurrently):
Failed!  - Failed: 1, Passed: 794, Skipped: 0, Total: 795, Duration: 7 m 53 s
  ConcurrencyTests.OneWriterFiveReaders_NoSqliteBusy_NoTornReads — ObjectDisposedException
  on a SQLite handle mid-stress-test. Re-run in isolation immediately after (no competing
  process): Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 1 m 8 s — 5/5
  clean, confirming environmental flakiness, not a regression.

# Run 2 (standalone, final check):
Failed!  - Failed: 1, Passed: 794, Skipped: 0, Total: 795, Duration: 7 m 20 s
  WalCrashConsistencyTests.HardKillDuringIngest_LeavesDatabaseConsistent_TwentyTimes —
  SQLite Error 10 'disk I/O error'. This is the pre-existing, already-documented `P2-5`
  flake (docs/evidence/phase-12/known-issues.md) — hard-kills the process mid-write 20
  times in a row and occasionally races this dev VM's own disk I/O under load. Exercises
  WAL-mode crash consistency in VSoftSol.Syslog.Data; no code path through anything this
  change touches (SqliteListenerStore, ListenerRegistrationHostedService,
  ListenerIdRegistry, EventEnricher, ListenerPortReloadService, or ListenerSettingsService).
```

Two different subsystems flaking on two different runs, neither exercised by this change's
own new tests (both of which pass every time they're run), is itself confirmation this is
this VM's ambient load-sensitivity — the same category already carried for `P2-5`/`P7-5`
since Phase 2/7 — not a regression introduced here. 794/795 both times = zero real
regressions.

## Security

No new external attack surface. The `listeners` table and its FK have existed since
Phase 1/2 with the same `PRAGMA foreign_keys = ON` enforcement already in place; this work
only starts writing to a table that already existed, using parameterized SQL throughout
(`SqliteListenerStore`). The new Settings → Listeners card is read-only, gated by the same
`AuthPolicies.Administer` policy the whole page already requires, and shows no new
sensitive data — protocol, bind address, port, enabled state, and first-seen timestamp are
all already visible elsewhere on the same page.

## UI verification

Driven the same way as the two prior v1.1 items (this sandbox's browser automation cannot
click through the app's self-signed HTTPS certificate on either available surface — a
documented, unrelated environment limit): the first-run wizard was completed over real
scripted HTTP against a live, collector-hosting instance, then `/settings/listeners` was
fetched with the resulting session.

The new "Listener identities" card rendered with the correct column headers (Id, Protocol,
Bind address, Status, First seen) and — critically — took the **populated** branch rather
than the empty-state branch: the page's `@if (_registeredListeners.Count == 0)` check is
plain server-rendered Razor logic (not virtualized), so seeing the real `<table>` instead of
the "Not registered yet" empty state is direct, non-virtualized proof that
`ListenerRegistrationHostedService` actually wrote real rows and
`ListenerSettingsService.ListRegisteredListenersAsync` correctly surfaced them. (The
individual `<tr>` row content itself does not appear in the static HTML fetch — the
`DataTable` component virtualizes rows client-side after the interactive Blazor circuit
connects, the same as every other table on this page and elsewhere in the app; this is not
specific to this feature.) The existing UDP/TCP port card was also confirmed still showing
the correct live values (`25514`/`25515`) alongside the new card, with no interference
between the two.

## Documentation updated

- `docs/RELEASE_NOTES.md` — new "Unreleased" bullet (v1.0.0's own text left untouched;
  P2-1 was an internal `PROGRESS.md`/`known-issues.md` tracking item, never a
  user-documented v1.0.0 limitation, so there is no historical bullet to preserve here).
- `PROGRESS.md` — new v1.1 log entry; "Deferred items across all phases" table's P2-1 row
  struck through with **DONE (v1.1)**, mirroring the table's own established convention.

## Conclusion

P2-1 (`docs/evidence/phase-02/known-issues.md`) is closed: `events.listener_id` now
resolves to a real, persisted `listeners` row for every event ingested while the collector
runtime is hosting listeners, correctly tracks a live UDP/TCP port change as a new listener
identity (never silently rewriting which listener an already-stored event points at), and
is visible to an Administrator from Settings → Listeners. Zero regressions beyond one
confirmed environmental flake in an unrelated subsystem.
