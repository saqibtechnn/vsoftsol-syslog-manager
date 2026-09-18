# v1.1 — CurrentUserAccessor crash on minimal-API endpoints — verification

## Build

```
$ dotnet build -c Release
Build succeeded.
    2 Warning(s)   (expected VSOFTSOL-UPDATE-001 — no release-signing key configured)
    0 Error(s)
```

## Targeted tests (new + self-update, for fast feedback)

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~SetupEndpointsWebTests|FullyQualifiedName~ConfigBundleWebTests|FullyQualifiedName~ReportWebTests|FullyQualifiedName~Updates" --no-build
Passed!  - Failed:     0, Passed:    45, Skipped:     0, Total:    45, Duration: 12 s
```

## Full regression

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --no-build
Passed!  - Failed:     0, Passed:  1113, Skipped:     0, Total:  1113, Duration: 1 m 16 s

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --no-build
Failed!  - Failed:     1, Passed:   853, Skipped:     0, Total:   854, Duration: 8 m 59 s

$ dotnet format --verify-no-changes
(exit 0 — no diffs)
```

`854` integration tests is `848` (v1.1-self-update's own full-suite number) plus the 6 new
tests added here (`ConfigBundleWebTests` × 2, `ReportWebTests` × 2,
`SetupEndpointsWebTests` × 2). The one failure is `WalCrashConsistencyTests.
HardKillDuringIngest_LeavesDatabaseConsistent_TwentyTimes` (`P2-5`) — a pre-existing,
already-documented, load-sensitive flake (random 15–120 ms process-kill timing under this
shared dev VM's contention), not a regression: this fix touches only Web-layer minimal-API
endpoint handlers and admin services, nothing in the SQLite/WAL/crash-consistency data path.
Confirmed non-regression by re-running the test in isolation:

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~WalCrashConsistencyTests" -v normal
Passed VSoftSol.Syslog.IntegrationTests.Data.WalCrashConsistencyTests.HardKillDuringIngest_LeavesDatabaseConsistent_TwentyTimes [13 s]
Total tests: 1
     Passed: 1
```

## Scope check — no other minimal-API endpoint has the same pattern

Every `Map*Endpoints` file in `src/VSoftSol.Syslog.Web/` was checked for a service method
reached from an endpoint handler that also calls `CurrentUserAccessor`/`_users.GetAsync()`
internally:

- `UpdateEndpoints` → `UpdateAdminService.GetVerifiedDownloadAsync` — already fixed (ADR 0021).
- `BundleEndpoints` → `ConfigBundleAdminService.ExportAsync` — fixed here.
- `ReportEndpoints` → `ReportAdminService.GetAsync` + `ReportRenderService.RunNowAsync` — fixed here.
- `SetupEndpoints` → its inline `/api/setup/first-message-status` handler — fixed here.
- `AuthEndpoints` — checked; predates any authenticated-user concept by definition, no such call.
- `SearchEndpoints` → `ExportAsync` already built `CurrentUser` from `HttpContext.User`
  directly (the correct pattern, confirmed by inspection) — never affected.

No further instances of this pattern remain.
