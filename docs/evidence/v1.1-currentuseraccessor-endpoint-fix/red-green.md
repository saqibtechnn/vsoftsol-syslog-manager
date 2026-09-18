# v1.1 — CurrentUserAccessor crash on minimal-API endpoints — red/green evidence

Per TESTING_STANDARDS.md §2.1. Found live, in production, by the user clicking "Export" on
Settings → Config bundles — not by this project's own test suite, which had never exercised
this route (or the "Run now" report download route) over real HTTP.

## RED — real production failure

```
2026-09-18T13:31:06.777Z [ERR] Microsoft.AspNetCore.Diagnostics.ExceptionHandlerMiddleware
An unhandled exception has occurred while executing the request.
{"RequestPath":"/bundles/export", ...}
System.InvalidOperationException: Do not call GetAuthenticationStateAsync outside of the DI
scope for a Razor component. Typically, this means you can call it only within a Razor
component or inside another DI service that is resolved for a Razor component.
   at Microsoft.AspNetCore.Components.Server.ServerAuthenticationStateProvider.GetAuthenticationStateAsync()
   at VSoftSol.Syslog.Web.Security.CurrentUserAccessor.GetAsync()
   at VSoftSol.Syslog.Web.Hardening.ConfigBundleAdminService.ExportAsync(...)
   at VSoftSol.Syslog.Web.Hardening.BundleEndpoints.ExportAsync(HttpContext http, ConfigBundleAdminService admin)
```

Root cause: `CurrentUserAccessor` wraps `AuthenticationStateProvider.
GetAuthenticationStateAsync()`, and this app's concrete provider
(`SyslogAuthenticationStateProvider`) derives from Blazor Server's
`RevalidatingServerAuthenticationStateProvider` → `ServerAuthenticationStateProvider`, whose
base implementation only works inside a Razor component's circuit DI scope — never from a
plain minimal-API endpoint handler (`MapGet`/`MapPost`). This is the exact same root cause
already fixed once, for `UpdateAdminService.GetVerifiedDownloadAsync`, while building v1.1's
self-update feature (`docs/evidence/v1.1-self-update/red-green.md`) — that fix flagged this
exact class of bug (a follow-up task was spawned) but had not yet been applied here when the
user hit it live.

Given the confirmed live failure, checking for the same pattern elsewhere in the codebase
found two more instances:

- `ReportRenderService.RunNowAsync` — called exclusively from `ReportEndpoints.RunAsync`
  (the "Run now" report download route; the `Reports.razor` button is a full page
  navigation to that route, never a circuit-scoped call) — also called
  `CurrentUserAccessor.GetAsync()`, and used the result (`user.Scope`) to determine *what
  data the report could see*, not just who to audit. `ReportAdminService.GetAsync` (called
  first, to look up the report) had the identical problem via its private
  `CurrentUserIdAsync` helper. Every "Run now" report download in this product was broken
  the same way.
- `SetupEndpoints`'s `/api/setup/first-message-status` — polled repeatedly by the "Waiting
  for messages" first-run page's client-side script to auto-advance once the collector
  stores its first event. Every single poll 500'd, the identical root cause.

Three new tests were written to reproduce all three failures, then the pre-fix source was
temporarily restored (`git stash`) to confirm each fails for the right reason before the
fix was reapplied:

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ConfigBundleWebTests|FullyQualifiedName~ReportWebTests" --no-build
...
Failed VSoftSol.Syslog.IntegrationTests.Hardening.ConfigBundleWebTests.Export_Administrator_Returns200_NotAServerError
  Expected the enum to be HttpStatusCode.OK {200} but found HttpStatusCode.InternalServerError {500}.
Failed VSoftSol.Syslog.IntegrationTests.Retention.ReportWebTests.RunNow_Administrator_Csv_Returns200_NotAServerError
  Expected the enum to be HttpStatusCode.OK {200} but found HttpStatusCode.InternalServerError {500}.

Failed!  - Failed:     2, Passed:     2, Skipped:     0, Total:     4, Duration: 1 s
```
(The 2 passes are each file's "unauthenticated → redirect to /login" case, unaffected by the
bug — correctly green from the first run.)

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~SetupEndpointsWebTests" --no-build
...
Failed VSoftSol.Syslog.IntegrationTests.Security.SetupEndpointsWebTests.FirstMessageStatus_AuthenticatedUser_Returns200_NotAServerError
  Expected the enum to be HttpStatusCode.OK {200} but found HttpStatusCode.InternalServerError {500}.

Failed!  - Failed:     1, Passed:     1, Skipped:     0, Total:     2, Duration: 977 ms
```

## Fix

`ConfigBundleAdminService.ExportAsync` and `ReportRenderService.RunNowAsync` now take the
caller's `ClaimsPrincipal` directly (from `HttpContext.User`, passed in by their respective
minimal-API endpoints) instead of resolving it through `CurrentUserAccessor`.
`ReportAdminService.GetAsync` gained a second overload that does the same, since it is
called from both the endpoint (needs the principal) and the Reports settings page's own
circuit (keeps using `CurrentUserAccessor` — unaffected, unchanged). `SetupEndpoints`'s
inline handler now takes `HttpContext http` directly and builds `new CurrentUser(http.User)`
— matching the convention `SearchEndpoints.ExportAsync` already used correctly (confirmed
by inspection: it was never affected, having done this right from the start). No other
method on any of these services needed to change: every one of them is only ever called
from within a Razor component's circuit.

**Scope check performed**: every `Map*Endpoints` file in `src/VSoftSol.Syslog.Web/` was
checked for a service method reached from an endpoint handler that also calls
`CurrentUserAccessor`/`_users.GetAsync()` internally — `AuthEndpoints` has no such call at
all (it predates any authenticated-user concept by definition); `SearchEndpoints` already
used the correct `HttpContext.User`-direct pattern. No further instances of this bug remain.

## GREEN

```
$ dotnet build -c Release
Build succeeded. 0 Errors.

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~SetupEndpointsWebTests|FullyQualifiedName~ConfigBundleWebTests|FullyQualifiedName~ReportWebTests|FullyQualifiedName~Updates" --no-build
Passed!  - Failed:     0, Passed:    45, Skipped:     0, Total:    45, Duration: 12 s
```

See `verification.md` for the full-suite regression result.
