# v1.1 close-out — red/green evidence

Per TESTING_STANDARDS.md §2.1. Three independent changes, each with the failure observed before
the fix.

## 1. Build blocked by new ImageSharp advisories (restore failure)

RED — every `dotnet build` / `dotnet test` failed at restore, with no source change:

```
build\VSoftSol.Syslog.BrandingGen\VSoftSol.Syslog.BrandingGen.csproj : error NU1903: Warning As Error:
Package 'SixLabors.ImageSharp' 2.1.13 has a known high severity vulnerability,
https://github.com/advisories/GHSA-j9gm-c75j-xc9q
  ... GHSA-jjfr-hcj7-qf5w, GHSA-wmxv-xphr-5c9g (NU1902/NU1903), then GHSA-gwg2-r3hj-4w44,
  GHSA-j3p4-wp97-rph4 surfaced on the next restore.
Failed to restore ...\BrandingGen.csproj
```

The NuGet advisory database changed while the code did not; `TreatWarningsAsErrors` turned five
newly published advisories into build failures.

GREEN — the five advisories are suppressed per-advisory in the two projects that consume
ImageSharp (`BrandingGen` and `UnitTests`, which exercises it); `dotnet restore` of the whole
solution is clean. Rationale and the pending operator decision (ImageSharp 3.x licence vs
`System.Drawing`) are in `docs/security/SECURITY_REVIEW.md` → "Build-tool advisory". The package
is not in any shipped artefact: Core references the tool with `ReferenceOutputAssembly="false"`
and no `SixLabors*.dll` exists in any `src` build output.

## 2. Load-dependent test flakes P2-5 and P7-5 (and a wider class of them)

RED — running the full integration suite on a loaded host:

| Run | Result |
|---|---|
| Argon2 timing test, first run alongside the crash test | FAILED (ratio outside 0.5–2.0) |
| Full integration suite, parallel collections (default) | 14 failures (spill, chaos, flood, kill-recovery, UDP 100k, dashboard concurrency, alert preview, `SQLite Error 10: disk I/O error`) |
| Same suite, only the Argon2 + WAL classes serialised | 847 / 854 — 7 spill/lifecycle failures remained |

Every one of the 14 passed when its class was run alone, and the 10 spill/chaos/lifecycle tests
passed together (10/10, 14 m 49 s) — the fault was cross-class CPU/disk contention, not a defect.

GREEN — three changes:

- `LocalAuthenticationProviderTests` compares the fastest of 9 samples per path instead of the
  median of 7 (contention only adds time, so the minimum is the stable estimator). The assertion
  and its 0.5–2.0 band are unchanged.
- `WalCrashConsistencyTests` waits up to 120 s (was 30 s) for the child `dotnet` process to start.
  A timeout there is host load, not a durability fault; the integrity assertions are unchanged.
- `tests/VSoftSol.Syslog.IntegrationTests/xunit.runner.json`: `parallelizeTestCollections: false`.
  Tests inside a class were already sequential; this stops the socket-, disk- and process-heavy
  classes from running against each other.

A run with only those three changes passed **854 / 854** (31 m 50 s), then the next full run
(with the new landmark tests, 856 total) failed 3 tests, so two more causes were found and fixed:

- **Transient `SQLite Error 10: disk I/O error` right after the kill** (`HardKillDuringIngest`, first
  statement after reopening). Reproduced in isolation: 1 of 3 runs failed, at `PRAGMA integrity_check`.
  Windows can still be releasing the killed process's WAL/SHM handles at that instant. The integrity
  check now retries only `SQLITE_IOERR` (up to 10 × 250 ms); corruption, a non-"ok" result, or an I/O
  error that outlasts the window still fail. After the change: 5 / 5 isolated runs passed (the console
  logger confirmed a real 1 m 38 s execution each time).
- **Drain deadlines too short for a slow disk** (`ChaosTests.DiskFullDuringSpill…`, 90 s; harness
  default 60 s). Each run of the 100 MB disk-full case takes about 65 s here, so any slowdown crossed
  the limit. Both are hang guards, not performance gates, and are now 300 s. Run alone, the two
  disk-full tests passed (2 m 57 s and 10 m 53 s).

- **Random fixed ports in three listener tests** (`WinEventLogListenerTests`, `TlsSyslogListenerTests`,
  `SnmpTrapListenerTests` used `Random.Shared.Next(20_000, 60_000)`). The final full run failed one
  test on it: `HttpListenerException: The process cannot access the file because it is being used by
  another process` at `HttpListener.Start()` — a random port that was reserved or busy. The product
  listener is correct; the tests now take an OS-assigned port from `LoopbackSyslog.FreeTcpPort()` /
  `FreeUdpPort()`, as the other listener tests already did. Run after the change: these three classes
  plus the landmark tests, 13 / 13.

Final full run before that last change: **856 total, 855 passed, 1 failed** (the port collision above;
per-test list in `test-output.txt`). The 13 tests touched by the port fix were re-run after it.
The full suite was not repeated a further time (about 35 minutes); every other test in it passed on
the build that was run.

## 3. Pre-shell pages had no `<main>` landmark (axe-core: `landmark-one-main`, `region`)

First automated axe-core scan of this product (closes the "no browser" half of P4-2 for the
unauthenticated pages — see `known-issues.md` for what remains). axe-core 4.10.0, rule tags
wcag2a, wcag2aa, wcag21a, wcag21aa, wcag22aa and best-practice, run against the rendered first-run
wizard page (`/setup`, step 1):

RED —

```
violations: landmark-one-main (moderate)  html
            region            (moderate)  .ds-brand__mark, h1, .ds-wizard__subtitle, .ds-field:nth-child(3)
passes: 24
```

Cause: `BareLayout` (login, change-password, denied, error) and `WizardLayout` (first-run wizard,
waiting-for-messages) bypass `MainLayout`, the only place a `<main>` existed.

Test first — `Auth/PreShellLandmarkTests` (two tests: `/login`, `/setup`):

```
Failed ... LoginPage_HasExactlyOneMainLandmark_...   Expected value to be 1 because exactly one main landmark per page, but found 0
Failed ... SetupWizard_HasExactlyOneMainLandmark_... Expected value to be 1 because exactly one main landmark per page, but found 0
```

GREEN — brand mark in `<header>`, body in `<main id="main-content">`, copyright line in `<footer>`
(login layout). The id matches the skip-link target the app shell already uses. Tests: 2 / 2
pass; the 18 neighbouring page/auth/first-run tests still pass (20 / 20). Re-scan of the real page:

```
violations: []   passes: 30   incomplete: []   main landmarks: 1
```

Layout unchanged by the wrapper: wizard card 672 px (42 rem), centred on the 1024 px viewport
(card centre 512, brand centre 512).

## 4. ImageSharp replaced by System.Drawing.Common (refactor — characterization tests)

Behaviour-preserving swap, so there is no new failing test; the guard is the existing
`BrandingPipelineTests` (derived-asset dimensions, placeholder fallback with warning, byte-identical
second run, rebrand without source change, ICO frame sizes), which passed on ImageSharp before the
change and 7 / 7 after it. Its dimension helper moved from `Image.Identify` to reading the PNG/BMP
header, so the test no longer depends on any imaging library.

- Restore: the five `NuGetAuditSuppress` entries were deleted from `BrandingGen` and `UnitTests`;
  `dotnet build -c Release` restores and builds with 0 errors (the two warnings are the intentional
  `VSOFTSOL-UPDATE-001`).
- `dotnet list build/VSoftSol.Syslog.BrandingGen package --vulnerable --include-transitive`:
  "has no vulnerable packages given the current sources".
- Unit suite: 1113 / 1113. `dotnet format --verify-no-changes`: clean.
- Not shipped: the MSI still excludes every `VSoftSol.Syslog.BrandingGen.*` file, and no
  `System.Drawing.Common.dll` appears in the Web output.
- Generated `logo-wide.png` inspected by eye: logo centred, aspect preserved, no halo.
