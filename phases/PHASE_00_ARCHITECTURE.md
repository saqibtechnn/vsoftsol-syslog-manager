# PHASE 0 — Architecture & Skeleton

## Context
Fresh repository. You are starting VSoftSol Syslog Manager v1.0.0, an on-premises
Windows syslog collection and log management application. Read `CLAUDE.md` and
`BUILD_PLAN.md` before doing anything. This phase produces a compiling skeleton and the
decisions that govern every later phase. **No feature code.**

## Objective
A solution that builds clean, with every project, seam, and convention in place, plus
written ADRs justifying each stack choice.

## Build
1. `VSoftSol.Syslog.sln` with the exact project layout in CLAUDE.md.
2. `Directory.Build.props` setting: .NET 8, C# 12, nullable enabled,
   `TreatWarningsAsErrors=true`, deterministic builds, shared version property.
3. `Core` project containing only: domain models for the canonical event schema in
   BUILD_PLAN.md, enums (`Facility`, `Severity`, `Protocol`, `ParseStatus`, `Role`),
   and the two seam interfaces — `ILogRepository` and `IAuthenticationProvider`.
   Both fully specified, neither implemented.
4. Empty class-library shells for `Data`, `Ingestion`, `Rules`, `Reporting`, wired with
   correct inward-only project references.
5. `Service` project as a Windows Service host with generic-host bootstrapping, Serilog
   configured to rolling file + Windows Event Log, and a no-op hosted service.
6. `Web` project as a Blazor Server app that starts, serves one placeholder page over
   HTTPS, and shares the `Service` DI container registrations.
7. Three test projects referencing xUnit + FluentAssertions, each with one passing
   smoke test so the harness is proven.
8. `docs/adr/` with one ADR per decision: SQLite over embedded Postgres, Blazor Server
   over SPA, Channels over an external queue, Zstd over gzip, single-service over
   split collector/UI processes. Each ADR states the decision, the alternatives
   rejected, and the cost being accepted.
9. `.editorconfig`, `.gitignore` for .NET, and a `dotnet format` clean tree.
10. **Branding pipeline** per `BRANDING.md` — build it now so no later phase is tempted
    to hardcode a string or an image:
    - `build/branding.targets`, imported by `Directory.Build.props`, running before
      compile
    - Reads `branding/logo.png` and `branding/brand.json`; derives every missing asset
      with ImageSharp (favicon, app icon, wide logo, mono logo, both installer BMPs)
    - Emits to `src/VSoftSol.Syslog.Web/wwwroot/branding/` and `installer/assets/`,
      both git-ignored
    - Generates `BrandingInfo.g.cs` in `Core` with product name, vendor name, vendor URL,
      support email, copyright, primary and accent colours as constants
    - Writes `--brand-primary` and `--brand-accent` CSS custom properties for Phase 4
    - Missing `logo.png` → build **warning** plus placeholder fallback from
      `branding/placeholder/`, never a build failure
    - Idempotent: two consecutive builds produce byte-identical derived assets

## Do not build in this phase
Any listener, parser, database schema, UI page beyond the placeholder, or repository
implementation. Do not add interfaces beyond the two named seams.

## Tests to write first
One smoke test per test project asserting the harness runs. Plus an architecture test
using NetArchTest asserting `Core` has no project references and no `System.Data` or
`System.Net` usings.

Branding tests:
- Build with `branding/logo.png` present → assert every derived asset is emitted at the
  correct dimensions.
- Build with `logo.png` removed → assert a warning is raised, placeholders are used, and
  the build still succeeds.
- Assert two consecutive builds produce byte-identical derived assets.
- **Literal test**: grep `src/` for the product and vendor names; assert the only hit is
  `BrandingInfo.g.cs`. This test runs in every later phase and is your guard against
  hardcoding.

## Verification — run these and paste output
```bash
dotnet build -c Release            # zero warnings
dotnet test                        # all green, including the architecture test
dotnet format --verify-no-changes
dotnet run --project src/VSoftSol.Syslog.Web   # confirm HTTPS placeholder page loads
```
Then swap in a different `logo.png`, rebuild, and confirm the derived assets change with
no source edit. That is the acceptance test for the branding pipeline.

## Validation & Evidence (per `TESTING_STANDARDS.md`)

This phase builds the machinery every later phase's validation depends on. Get it right
here or every subsequent gate is unenforceable.

- **CI pipeline** — `.github/workflows/ci.yml` implementing §7 in full: build (warnings
  as errors), format check, test, coverage threshold, vulnerability scan, architecture
  fitness tests, branding literal test. It must **fail** on each, proven by deliberately
  breaking one of each and showing the red run.
- **Architecture fitness tests** (NetArchTest) as permanent guards, not one-off checks.
- **Build reproducibility** — two clean builds produce byte-identical assemblies.
- **Test harness proof** — deliberately break one smoke test and show it goes red. A
  harness that has never failed is not a harness.
- **Stryker.NET and FsCheck wired in now**, configured but running against trivial
  targets, so Phases 3 / 7 / 10 have no excuse to skip mutation and property tests.
- **Evidence:** `docs/evidence/phase-00/` with the CI config, the deliberate-failure run
  output, and the reproducibility hash comparison.

## Security Validation (per `SECURITY_STANDARDS.md`)

- **`docs/security/THREAT_MODEL.md`** — STRIDE across all five trust boundaries in §3.
  This is a Phase 0 deliverable, not a Phase 11 one; the architecture is being decided now.
- **`docs/security/ASVS-checklist.md`** — OWASP ASVS 5.0 L2, every control marked
  planned / implemented / not-applicable-because.
- **Security tooling in CI, proven to fail**: CodeQL + Security Code Scan (SAST),
  `dotnet list package --vulnerable` + Trivy (SCA), Gitleaks over full history, CycloneDX
  SBOM generation. Deliberately introduce one finding of each type and show CI go red.
- **Analyzer rules as build failures**: string-concatenated SQL, `Process.Start` with
  unvalidated input, disabled certificate validation, weak crypto, `catch {}`.
- **Least-privilege design ADR** — the service account model, data-directory ACLs, and
  which operations need which rights. Decide now; retrofitting privilege separation later
  is expensive.
- **Evidence:** `docs/evidence/phase-00/security/` with the threat model, ASVS baseline,
  and the deliberate-failure CI runs.

## Definition of Done
Every box in CLAUDE.md's Definition of Done, plus: the ADR set explains every choice,
and the architecture test fails if someone later adds an outward reference from `Core`.

## Commit
`chore: phase 0 — solution skeleton, seams, ADRs` → tag `v1.0.0-phase.0`
