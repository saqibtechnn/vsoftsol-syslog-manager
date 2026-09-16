# v1.1 — Self-update check and verified download (ADR 0021) — red/green evidence

Per TESTING_STANDARDS.md §2.1. This feature's full test set spans
`tests/VSoftSol.Syslog.UnitTests/Updates/` (`ProductVersionTests`,
`UpdateManifestValidatorTests`, `UpdateSignatureVerifierTests` — 41 tests) and
`tests/VSoftSol.Syslog.IntegrationTests/Updates/` (`Migration011Tests`,
`SqliteUpdateSettingsStoreTests`, `GitHubUpdateClientTests`, `UpdateCheckerTests`,
`UpdateWebTests` — 39 tests), written test-first against no prior implementation, across a
single continuous work session. The two RED/GREEN pairs below are the two genuine
regressions this session directly observed and fixed via real tool output — both were real
defects the tests correctly caught, not authoring mistakes in the tests themselves.

## RED #1 — a real architecture-layering violation

Adding `GitHubUpdateClient`'s SSRF guard by giving `VSoftSol.Syslog.Data` a new
`ProjectReference` to `VSoftSol.Syslog.Rules` (to reuse `Rules.Actions.PrivateNetworkGuard`)
passed the check this session verified by hand (`CoreArchitectureTests` — no `System.Net` in
`Core`) but violated a broader, separately-existing fitness test this session had not yet
read:

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release
...
Failed!  - Failed:     1, Passed:  1112, Skipped:     0, Total:  1113, Duration: 1 m 20 s
  VSoftSol.Syslog.UnitTests.Architecture.LayeringTests.Layer_DoesNotReference_ForbiddenAssemblies
    (layerAssemblyName: "VSoftSol.Syslog.Data", forbidden: [...,"VSoftSol.Syslog.Rules",...])
  referenced.Should().NotContain(forbidden) failed — "VSoftSol.Syslog.Rules" was referenced.
```

**Fix** (ADR 0021, decision #5): reverted the `Data → Rules` project reference; added a
small, deliberately duplicated, internal `VSoftSol.Syslog.Data.Updates.PrivateNetworkGuard`
(doc-commented to point at the `Rules`-layer original and at this ADR). `UpdateChecker`
itself also needed `Rules.Actions.NotificationSink`, so it was moved from `Data` to
`VSoftSol.Syslog.Service.Hosting` — the layer every other `NotificationSink` consumer
already lives in.

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release
Passed!  - Failed:     0, Passed:  1113, Skipped:     0, Total:  1113, Duration: 1 m 8 s
```

## RED #2 — `CurrentUserAccessor` crashes outside a Razor component circuit

`UpdateWebTests.Download_ReadyAndUntampered_StreamsTheMsiAndWritesTheAuditLog` — the first
real-HTTP (`WebApplicationFactory`) test ever written against a Web download endpoint that
also needs to audit the acting user (no prior test in this codebase exercised
`/bundles/export` or a report download over real HTTP either) — failed with a live 500:

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~Updates"
...
Failed VSoftSol.Syslog.IntegrationTests.Updates.UpdateWebTests.Download_ReadyAndUntampered_StreamsTheMsiAndWritesTheAuditLog
  Expected HttpStatusCode.OK {200} but found HttpStatusCode.InternalServerError {500}.
  (temporary diagnostic: response body was)
  System.InvalidOperationException: Do not call GetAuthenticationStateAsync outside of the
  DI scope for a Razor component. Typically, this means you can call it only within a Razor
  component or inside another DI service that is resolved for a Razor component.
     at Microsoft.AspNetCore.Components.Server.ServerAuthenticationStateProvider.GetAuthenticationStateAsync()
     at VSoftSol.Syslog.Web.Security.CurrentUserAccessor.GetAsync()
     at VSoftSol.Syslog.Web.Updates.UpdateAdminService.GetVerifiedDownloadAsync(CancellationToken ct)
     at VSoftSol.Syslog.Web.Updates.UpdateEndpoints.DownloadAsync(HttpContext http, UpdateAdminService admin)

Failed!  - Failed:     1, Passed:    38, Skipped:     0, Total:    39, Duration: 12 s
```

A real, previously-latent bug: `CurrentUserAccessor` wraps `AuthenticationStateProvider.
GetAuthenticationStateAsync()`, and this app's provider (`SyslogAuthenticationStateProvider`)
derives from Blazor Server's `RevalidatingServerAuthenticationStateProvider` /
`ServerAuthenticationStateProvider`, whose base implementation only works inside a Razor
component's circuit DI scope — never from a plain minimal-API endpoint handler. This same
pattern exists in at least `ConfigBundleAdminService.ExportAsync` (called from
`BundleEndpoints`'s `/bundles/export`), flagged separately for its own follow-up rather than
fixed here — out of scope for this feature.

**Fix**: `UpdateAdminService.GetVerifiedDownloadAsync` now takes the caller's
`ClaimsPrincipal` directly (`HttpContext.User`, passed in by `UpdateEndpoints.DownloadAsync`)
instead of resolving it through `CurrentUserAccessor`. `SaveEnabledStateAsync`/
`CheckNowAsync` are both called from `UpdatesSettingsPage.razor`'s own circuit, so they were
left unchanged.

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~Updates" --no-build
Passed!  - Failed:     0, Passed:    39, Skipped:     0, Total:    39, Duration: 10 s
```

## Full regression, after both fixes

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release --no-build
Passed!  - Failed:     0, Passed:  1113, Skipped:     0, Total:  1113, Duration: 1 m 16 s

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~Updates" --no-build
Passed!  - Failed:     0, Passed:    39, Skipped:     0, Total:    39, Duration: 10 s
```

See `verification.md` for the full-suite and build-gate results.
