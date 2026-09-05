# PROGRESS

Claude Code updates this file at the end of every phase. A fresh session reads it first
to learn where the build stands. Keep it terse and factual.

---

## Current state

- **Last completed phase:** 0 — Architecture & skeleton
- **Last tag:** `v1.0.0-phase.0`
- **Next phase:** 1 — Data layer
- **Build status:** green — `dotnet build -c Release` warning-clean, `dotnet test` 32/32, `dotnet format` clean
- **Branding:** `branding/logo.png` present — yes (788 KB); `branding/brand.json` present; `branding/placeholder/logo.png` committed

---

## Phase log

<!-- Append one block per completed phase. Newest at the top. -->

### Phase 0 — Architecture & skeleton — 2026-09-05 — tag `v1.0.0-phase.0`

**Shipped**
- `VSoftSol.Syslog.sln` with the exact CLAUDE.md layout: `Core`, `Data`, `Ingestion`,
  `Rules`, `Reporting` (class libs), `Service` (Windows Service host / composition root),
  `Web` (Blazor Server), plus `UnitTests`, `IntegrationTests`, `Benchmarks`, and the
  build-only `build/VSoftSol.Syslog.BrandingGen`.
- `Directory.Build.props` / `.targets` / `Directory.Packages.props`: .NET 8, C# 12,
  nullable on, `TreatWarningsAsErrors`, deterministic + `ContinuousIntegrationBuild`,
  central package management (no floating versions), shared `1.0.0` version.
- `Core`: canonical event schema (`SyslogEvent`, `EventField`, `SyslogPriority`), enums
  (`Facility`, `Severity`, `Protocol`, `ParseStatus`, `Role`), and the two seams —
  `ILogRepository`, `IAuthenticationProvider` — fully specified, unimplemented. No I/O.
- Layer shells wired inward-only (each references `Core`, verified by fitness tests).
- `Service`: generic-host bootstrap, `AddWindowsService`, Serilog → rolling file +
  Windows Event Log, no-op `CollectorHostedService`, `AddSyslogPlatform` composition root.
- `Web`: Blazor Server, HTTPS-only, baseline security headers middleware, one placeholder
  page reading `BrandingInfo`, shares the `Service` DI registrations, `UseAntiforgery`.
- **Branding pipeline** (ADR 0007): `BrandingGen` console tool derives every omitted asset
  from `branding/logo.png` with ImageSharp, generates `BrandingInfo.g.cs` + `brand.css`,
  missing logo → warning + placeholder (never a build failure), byte-identical on rebuild.
- `.editorconfig` (security analyzer rules elevated to errors), `.gitignore`,
  `.gitattributes`, `.config/dotnet-tools.json`, `stryker-config.json`, `.gitleaks.toml`.
- `.github/workflows/ci.yml`: build (warnings=errors), format, test+coverage, SCA
  (fails on any vuln) + deprecated, CodeQL SAST, Gitleaks, CycloneDX SBOM, arch + literal
  gates, reproducible-build check — each shown to fail on violation.
- 8 ADRs (`docs/adr/0001`–`0008`), `docs/security/THREAT_MODEL.md` (STRIDE × 5
  boundaries), `docs/security/ASVS-checklist.md` (ASVS 5.0 L2 baseline),
  `docs/security/SECURITY_REVIEW.md`.

**Verification output** (`docs/evidence/phase-00/`)
- `dotnet build -c Release` → Build succeeded, 0 Warning(s), 0 Error(s) (`build-output.txt`)
- `dotnet test` → 32 passed, 0 failed, 0 skipped (`test-output.txt`)
- `dotnet format --verify-no-changes` → exit 0
- `dotnet run --project src/VSoftSol.Syslog.Web` → HTTPS placeholder page HTTP 200, CSP +
  security headers present, `branding/brand.css` served
- Reproducible build: two clean builds → byte-identical assemblies (`reproducibility.txt`)
- Rebrand acceptance: swap `logo.png` + `brand.json` → derived assets and `BrandingInfo`
  change, **zero source edits** (`rebrand-acceptance.txt`)
- Deliberate-failure proofs for all four gates (`deliberate-failures.txt`)
- SCA clean (`security/sca-vulnerable.txt`)

**Decisions made**
- ADR 0001 SQLite over embedded Postgres · 0002 Blazor Server over SPA · 0003 Channels
  over external broker · 0004 Zstd over gzip · 0005 one service, not split processes ·
  0006 least-privilege service account + data-dir ACLs · 0007 branding pipeline + scope
  of the literal guard · 0008 exactly two seams.
- Roles: `Administrator`, `Operator`, `ReadOnly`, `Auditor` (PHASE_04).
- Literal guard targets brand **values** (product/vendor name, URLs, colours), not the
  mandated `VSoftSol.Syslog.*` namespace token — see ADR 0007. Rebrand acceptance proves
  Constraint 11's intent is met.
- `Core` takes a `ReferenceOutputAssembly="false"` project reference on `BrandingGen`
  for build ordering only; creates no IL dependency (fitness test verifies).
- ImageSharp pinned to the 2.1.x (Apache-2.0) line, not 3.x (split licence); 2.1.13
  clears the advisories that affected earlier 2.1.x.
- Transitive pins added for legacy `System.Net.Http` / `System.Security.Cryptography.*`
  dragged in by old test-only analyzers, to keep SCA clean.

**Sign-off block** (TESTING_STANDARDS.md §9)
```
PHASE 0 SIGN-OFF
  Tests added:            24 unit (incl. 2 property, 5 architecture/layering, 8 branding),
                          4 integration
  Total suite:            32 tests, 32 passing, 0 skipped
  Red-green observed:     yes  (docs/evidence/phase-00/red-green.md)
  Coverage:               ~57% overall (BrandingGen 87%, Core partial); threshold gate
                          deferred to Phase 1 — Ingestion/Rules/Reporting are shells
                          (known-issues P0-1)
  Mutation score:         N/A  — Stryker wired + configured; runner env issue on this
                          host, no mutable domain logic yet (known-issues P0-2)
  Performance gates:      N/A for Phase 0
  UX gate:                N/A — no functional screen shipped (placeholder page only;
                          first real UI is Phase 4)
  Regression:             N/A — first phase; full suite green
  Evidence committed:     docs/evidence/phase-00/
  Known issues:           5, listed in known-issues.md
  Security gate:          SAST (analyzers local + CodeQL in CI) PASS / SCA PASS /
                          secrets (manual + Gitleaks in CI) PASS / DAST N-A (no auth UI)
  Open findings:          0 C, 0 H, 0 M, 1 L   (P0-3: CSP unsafe-inline on style-src,
                          fixed in Phase 4)
```

**UX gate**: N-A — Phase 0 ships only the unstyled placeholder page explicitly permitted
by the phase prompt; the five-point gate applies from Phase 4.

**Deferred**
- [ ] P0-1: enforce line-coverage ≥ 80% on Ingestion/Rules/Reporting — from Phase 1.
- [ ] P0-2: get Stryker completing a run (VsTest adapter env) — Phase 3, first real target.
- [ ] P0-3: remove CSP `'unsafe-inline'` (`style-src`) with nonces — Phase 4.

**Known issues**
- See `docs/evidence/phase-00/known-issues.md` (P0-1 … P0-5). No `TODO(phase-N)` markers
  in shipping code.

---

## Open decisions needing the operator

- **P0-3 sign-off** (Low): accept CSP `'unsafe-inline'` on `style-src` until Phase 4?
  Recorded in `docs/security/SECURITY_REVIEW.md` awaiting operator initials.
- **Environment**: the build machine had no .NET SDK; .NET 8.0.424 was installed to
  `%USERPROFILE%\.dotnet` (user-local, added to user PATH). WiX (Phase 12) is not yet
  installed.

---

## Deferred items across all phases

| Marker | Where | Target phase |
|---|---|---|
| _(none — no `TODO(phase-N)` in code)_ | | |
| P0-1 coverage gate | CI `ci.yml` | 1 |
| P0-2 Stryker run | `stryker-config.json` | 3 |
| P0-3 CSP nonces | `SecurityHeadersMiddleware` | 4 |
