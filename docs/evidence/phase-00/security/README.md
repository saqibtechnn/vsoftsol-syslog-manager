# Phase 0 — Security evidence

SECURITY_STANDARDS.md §8. What was verified locally for the phase tag, and what runs in
CI on every commit.

## Verified locally (this machine, .NET SDK 8.0.424)

| Gate | Command | Result | File |
|---|---|---|---|
| SCA — vulnerable packages | `dotnet list VSoftSol.Syslog.sln package --vulnerable --include-transitive` | **clean** — 0 Critical/High/Moderate/Low after transitive pins for legacy `System.*` | `sca-vulnerable.txt` |
| SAST (analyzers) | `dotnet build -c Release` with .NET analyzers + Roslynator + SecurityCodeScan, `TreatWarningsAsErrors` | **clean** — 0 warnings | `../build-output.txt` |
| Security analyzer rules are build errors | deliberate violations (outward ref, unused var) | build FAILS as designed | `../deliberate-failures.txt` |
| Secrets | manual review — no credentials, keys, or tokens in source or fixtures; `brand.json` holds only public URLs | clean | this file |
| Reproducible build | two clean builds, byte-compare assemblies | **identical** | `../reproducibility.txt` |

## Runs in CI (`.github/workflows/ci.yml`) — GitHub-hosted, not runnable on this box

| Gate | Job / tool |
|---|---|
| SAST | `sast` job — CodeQL (`security-and-quality`) + SecurityCodeScan |
| SCA | `build-test` job — `dotnet list --vulnerable` (fails on any) + `--deprecated` |
| Secrets, full history | `secrets` job — Gitleaks (`.gitleaks.toml`) |
| SBOM | `sbom` job — CycloneDX, uploaded as an artifact |
| Architecture + branding literal guards | `build-test` job |
| Reproducible build | `build-test` job — clean rebuild + hash compare |

DAST (OWASP ZAP) begins in Phase 4 when there is an authenticated UI to scan.

## Analyzer rules elevated to build errors (`.editorconfig`)

`CA2100` (SQL string concat), `CA5359` (disabled cert validation),
`CA5350/5351/5358` (weak crypto), `CA3006` (process command-line),
`RCS1075` (empty catch), `SCS0001/0002/0018/0026` (command/SQL injection, path traversal,
weak crypto).

## Least privilege

Design recorded in `docs/adr/0006-least-privilege-service-account.md`; enforced by the
installer and audited on a clean VM in Phase 12.
