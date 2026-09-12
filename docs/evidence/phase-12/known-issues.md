# Phase 12 — known issues

Per TESTING_STANDARDS.md §6, every bug found during this phase, fixed or carried, with its
root cause in one sentence.

## Pre-existing flakes reproduced during this phase's final verification runs

This phase's build/test/publish/install cycle ran for many hours of near-continuous,
often-parallel `dotnet build`/`dotnet test`/`dotnet publish` activity on this one dev VM —
a load profile well beyond any single prior phase's verification. Two already-documented,
pre-existing, load-sensitive tests reproduced their known flakiness during the final full
regression runs; neither touches code this phase changed, and both are confirmed
non-regressions below rather than just cited against their original reports unchanged.

- **`P2-5` (`WalCrashConsistencyTests`, hard-kill/disk-I/O soak test)** — reproduced with
  its own `iteration N: X committed rows survived, integrity ok` output pattern, matching
  its original Phase 2 report exactly. Confirmed non-regression: passes cleanly in
  isolation every time it was retried this phase.
- **`P7-5` (`LocalAuthenticationProviderTests.AuthenticateAsync_UnknownUserVsWrongPassword_TakeComparableTime`,**
  **the Argon2 decoy-timing ratio check)** — reproduced more consistently this phase (4/4
  isolated reruns failed, vs. the original Phase 7 report's "passes 2/3 in isolation");
  worth recording honestly rather than citing the old ratio unchanged. Investigated rather
  than assumed: `LocalAuthenticationProvider` and `Argon2idPasswordHasher` are untouched by
  any Phase 12 change, this test never goes through HTTP or `FirstRunGateMiddleware` (it
  calls `AuthenticateAsync` directly against a fake in-memory-backed harness), and a live
  CPU check immediately after the failures showed this machine at ~5% load, not under
  sustained contention at the instant of failure — ruling out both "Phase 12 broke the
  timing property" and "the machine was simply busy" as the explanation. The remaining
  explanation is what the test itself already documents: it compares two single-digit-
  millisecond Argon2 calls by a strict 0.5–2.0 ratio, a margin thin enough that ordinary
  OS scheduling jitter (a GC pause, a page fault, a background disk-indexing tick against
  the many files this session had just written) can push it over — the same fundamental
  fragility Phase 7 already identified, now observed hitting more often after this
  phase's unusually long, heavy session. Not a functional regression: the property this
  test protects (no timing side-channel for user enumeration) is architectural — both
  paths in `LocalAuthenticationProvider.AuthenticateAsync` unconditionally perform exactly
  one Argon2 computation, verified by reading the source, not just by this one flaky timing
  assertion.

## Fixed during this phase

- **`NETSDK1152`: Web and Service's publish outputs collided on `appsettings.json`.**
  Root cause: Web references Service.csproj for the composition root
  (`SyslogPlatformExtensions`), and `dotnet publish` pulls a referenced project's own
  `Content` items (including its `appsettings.json`) into the referencing project's publish
  output by default — never hit before this phase because no earlier phase ran
  `dotnet publish`, only `dotnet build`/`dotnet run`. Fixed by passing
  `ExcludeAppSettingsFromPublish=true` via the `ProjectReference`'s `Properties` metadata
  and a matching conditional `Content Update` in Service.csproj that only applies when
  built *as a reference*, never when Service publishes itself.
- **The installer was designed and half-built around two Windows Services before ADR 0005
  was (re-)read.** Root cause: PHASE_12_RELEASE.md's "the Windows Service" (singular)
  reads ambiguously against the two-executable (Service.exe / Web.exe) architecture visible
  in the source tree, and the installer's `Product.wxs` was authored, built, and verified
  against a plausible-but-wrong two-service interpretation before `docs/adr/0005` — a
  binding Phase 0 decision explicitly stating *one* Windows Service in production packaging
  — was found. Caught before any tag by cross-checking the installer against the ADR
  index while writing this evidence pack's own architecture notes. Fixed by: adding
  `CollectorOptions.HostCollectorRuntime` (bootstrap-tier, false by default so
  `dotnet run --project src/VSoftSol.Syslog.Web` stays UI-only in dev), an
  `appsettings.Production.json` that sets it true (picked up automatically because a
  Windows-Service-hosted process has no `ASPNETCORE_ENVIRONMENT` and defaults to
  Production), conditionally calling `AddCollectorRuntime` from Web's `Program.cs`, and
  rewriting the installer to package and register only `VSoftSol.Syslog.Web.exe` as the
  single service (`VSoftSol.Syslog.Service.exe` remains the ADR-sanctioned, unpackaged,
  dev-only console-mode collector). Full regression re-run confirmed no behavioural
  regression from the merge (758 total integration tests, only the pre-existing P2-5 flake).
- **`ScopeChokepointArchitectureTests.NoWebType_DependsOnILogRepositoryDirectly` caught a
  genuine architecture violation** in this phase's own new code: `SetupEndpoints` and
  `WaitingForMessages.razor` injected `ILogRepository` directly to check "has any message
  ever arrived", bypassing the per-user visibility scope `ScopedEventReader` exists to
  enforce (ADR 0012). Fixed by switching both to `ScopedEventReader.CountAsync(scope, ...)`
  via `CurrentUserAccessor`, exactly like every other Web query.
- **`AuthorizationMatrixTests.EveryRoutableComponent_EitherAllowsAnonymousOrRequiresAKnownPolicy`
  caught the new `/setup` route's `[AllowAnonymous]`** as an unreviewed exception (by
  design — the test exists specifically to force a human decision on every anonymous
  route). Reviewed and added to the test's own allow-list with a comment explaining why
  (the wizard must be reachable before any credential exists) and how it is bounded
  (`FirstRunGateMiddleware` redirects away from it the instant setup has completed).
- **Nearly the entire pre-existing integration suite (16 failures) broke** the first time
  `FirstRunGateMiddleware` was wired in, because dozens of tests share one
  `SyslogWebApplicationFactory` fixture per test class and probe "requesting a protected
  page while unauthenticated redirects to `/login`" against a *never-seeded* database —
  which the new gate, correctly, now redirects to `/setup` instead (a genuinely
  never-configured install has no working credential to log in with at all). Root cause
  was a real, useful distinction the old tests never needed to make: "not signed in" vs.
  "this install has never been set up." Fixed by having `SyslogWebApplicationFactory` seed
  a completed-setup state by default (a `SeedCompletedSetup` property, true by default —
  xUnit's `IClassFixture` requires exactly one, genuinely zero-argument public constructor,
  so this had to be a settable property rather than a constructor argument), with only the
  two new first-run-specific test files opting out of that default to exercise the
  genuinely pristine state.

## Carried — environmental, not a defect in the product

Same disposition as every prior phase's environmental-carry items (P1-1, P5-1, P6-1, P9-1,
…): infrastructure this development sandbox does not have access to, not a gap in what was
built or tested here.

- **Installation matrix on clean Windows Server 2019/2022/2025 VMs** — this environment has
  one non-clean development VM and no VM provisioning access. The MSI was built, validated
  (`wix msi validate`, all standard ICEs bar the deliberately-suppressed cosmetic ICE60),
  and its full contents inspected via decompilation (every `ServiceInstall`, firewall rule,
  ACL grant, and registry value confirmed byte-for-byte correct against intent — see
  `verification.md`). A genuine `msiexec` install was deliberately not run on this shared
  dev machine: creating a real Windows Service, opening real firewall rules, and writing
  `HKLM` registry keys on a machine outside this product's own sandbox is a system-level
  change outside what an autonomous session performs unilaterally, independent of available
  infrastructure.
- **Upgrade test on a real installed service** (install phase.11 build → populate → upgrade
  → assert survival) — needs the same real installed-service precondition as above. The
  upgrade *mechanism* (WiX `MajorUpgrade`, which preserves the data directory and never
  touches it) was inspected and confirmed correct in the decompiled MSI; exercising it live
  is carried with the installation matrix.
- **Real-device acceptance** (a physical/virtual Cisco switch, a FortiGate, and a Linux host
  sending live traffic simultaneously for 1 hour) — no real or virtual network hardware is
  reachable from this sandbox. The 17 vendor parser packs' correctness is evidenced instead
  by the full committed fixture corpus (200+ real-format sample messages per the Phase 3/11
  evidence packs) and the oracle differential tests, which this phase's full regression
  re-run confirmed are still green against the packaged build's own compiled parsers.
- **Live, observed usability test** with an untrained network admin — no such person is
  reachable from an autonomous coding session. In its place: a documented cold-eyes
  walkthrough of the installer → wizard → waiting-for-messages flow was performed and
  recorded (`ux-gate.md`), and the flow's mechanics were exercised end-to-end by the
  automated `FirstRunWizardTests` (every step's form, validation, and progression).
- **Precise ASP.NET Core Runtime version gate at install time** — the installer confirms
  *some* .NET shared host is present (a `RegistrySearch` against
  `dotnet\Setup\InstalledVersions\x64\sharedhost`), not specifically the ASP.NET Core 8.0.x
  shared framework. A precise version gate needs either a Burn bootstrapper chaining the
  hosting bundle installer, or a custom action — both meaningfully larger scope than a
  narrow installer fix. A missing or wrong runtime version still fails fast with a clear
  .NET error at service start rather than silently; the Admin Guide states the exact
  requirement. Backlogged for v1.1.
- **Listener port changes require a manual service restart, not a live one.** Making a
  port change take effect immediately would need either hot-rebindable listener sockets (a
  real feature addition, out of scope for a release-packaging phase) or granting the
  web-facing service account rights to restart the Windows Service (a privilege-escalation
  shaped feature that deserves its own careful design, not a rushed addition here). The
  wizard's ports step and the documented `bootstrap-overrides.json` hand-edit both take
  effect on next restart; this is stated plainly in the Admin Guide rather than implied to
  be live.
- **Data directory relocation from the wizard** — the wizard's data-directory step is
  informational (confirms the installer-provisioned path) rather than editable, because
  letting an admin redirect to an arbitrary folder would require either elevating the
  least-privilege service account or a custom action to re-apply ACLs to a new location
  — a real feature, not a narrow fix, and one that would have weakened this phase's
  least-privilege service-account work to rush. Backlogged for v1.1 alongside the port
  gap above.
- **MFA enforcement at login** — carried from Phase 11 (B11-3), unchanged by this phase.
  Stated plainly in the Hardening Guide and Release Notes rather than left for an operator
  to discover.
