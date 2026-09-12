# Phase 12 — red/green evidence

Per TESTING_STANDARDS.md §2.1. Each new capability's test was written first, observed
failing for the correct reason, then made to pass. Full command transcripts for each are in
this session's history; the compressed RED message and GREEN result are recorded here.

## `RetentionPresetsTests` (wizard retention step)

RED: `error CS0103: The name 'RetentionPresets' does not exist in the current context`
(6 call sites). GREEN after implementing `RetentionPresets`:
`Passed! - Failed: 0, Passed: 6, Skipped: 0, Total: 6`.

## `VendorSupportDocumentParserTests` (waiting-page device commands)

RED: `error CS0234: The type or namespace name 'VendorSupport' does not exist in the
namespace 'VSoftSol.Syslog.Core'`. GREEN after implementing the parser:
`Passed! - Failed: 0, Passed: 5, Skipped: 0, Total: 5` — plus
`VendorSupportDocumentParserRealDocumentTests` (parses the actual, checked-in
`VENDOR_SUPPORT.md`, not a fixture): `Passed! - Failed: 0, Passed: 8, Skipped: 0, Total: 8`.

## `BootstrapConfigOverridesTests` (bootstrap-tier port overrides)

RED: three `error CS0103: The name 'BootstrapConfigOverrides' does not exist` call sites.
GREEN: `Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3`.

## `WebServiceIdentityTests`

RED: `error CS0246: The type or namespace name 'WebServiceIdentity' could not be found`.
GREEN: `Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1` (later updated, RED→GREEN
again, when the single-service architecture correction changed the expected name from
`"VSoftSol Syslog Manager Web"` to plain `"VSoftSol Syslog Manager"`).

## `FirstRunStateTests`

RED: `error CS0246: The type or namespace name 'FirstRunState' could not be found` (3 call
sites). GREEN: `Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3`.

## `FirstRunGateWebTests` (end-to-end gate behaviour)

RED (genuine behavioural failure, not a compile error — the middleware did not exist yet):
```
Expected the enum to be HttpStatusCode.OK {value: 200}, but found HttpStatusCode.Found {value: 302}.
Expected string to be "/setup" ... but "https://localhost/login?returnUrl=%2Fdashboards" ...
```
GREEN after implementing `FirstRunGateMiddleware` and the minimal `/setup` page:
`Passed! - Failed: 0, Passed: 3, Skipped: 0, Total: 3`.

## `FirstRunWizardTests` (the full 5-step flow, end to end over real HTTP)

First real run surfaced a genuine framework-level bug, not a test-order problem: step 3
(the data-directory "Continue" step, which posts no fields) 500'd with
`System.InvalidOperationException: EditForm requires either a Model parameter, or an
EditContext parameter` — an `EditForm` bound to a model type with zero properties fails to
bind. Fixed by switching that one step to a plain `<form>` + `@onsubmit`/`@formname`
(exactly the pattern already used for step 5's file upload) instead of `EditForm`. Full run
after the fix: `Passed! - Failed: 0, Passed: 1, Skipped: 0, Total: 1`.

## Architecture-fitness tests catching real mistakes in this phase's own new code

Both of the following are pre-existing, phase-spanning fitness tests — not new tests
written for Phase 12 — that did exactly the job they exist for:

- `ScopeChokepointArchitectureTests.NoWebType_DependsOnILogRepositoryDirectly` failed after
  `SetupEndpoints`/`WaitingForMessages.razor` were first written (they injected
  `ILogRepository` directly). Fixed by routing both through `ScopedEventReader` with the
  caller's own `UserScope`, then the full integration suite (756 tests) passed with this as
  the only failure resolved.
- `AuthorizationMatrixTests.EveryRoutableComponent_EitherAllowsAnonymousOrRequiresAKnownPolicy`
  failed on `/setup`'s new `[AllowAnonymous]` attribute (`Expected collection {"/login",
  "/Error"} to contain "/setup"`) until it was added to the test's own reviewed allow-list
  with a comment explaining why.

## Pre-existing suite regressions surfaced (and fixed) by `FirstRunGateMiddleware`

16 pre-existing integration tests failed the first time the gate was wired into the request
pipeline — every one of them a test that probes "unauthenticated → redirected to `/login`"
against a database that had never been seeded with any credential at all, which the new
gate correctly redirects to `/setup` instead. Root-caused and fixed by giving
`SyslogWebApplicationFactory` a `SeedCompletedSetup` property (default true, matching every
pre-existing test's implicit assumption); see `known-issues.md` for the full account. Full
suite after the fix: `Passed! - Failed: 0, Passed: 158, Skipped: 0, Total: 158` for the
targeted regression set, and `Failed: 1 (pre-existing P2-5 flake), Passed: 755, Total: 756`
for the complete run.
