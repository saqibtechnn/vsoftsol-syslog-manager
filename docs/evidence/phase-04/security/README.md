# Phase 4 — security evidence

SECURITY_STANDARDS.md §8. What was run, and the disposition of what could not be.

## SAST — PASS
Roslyn analyzers + Security Code Scan run on every build with `TreatWarningsAsErrors`.
`dotnet build -c Release` → **0 warnings, 0 errors** across 13 projects (build-output in
`../test-output.txt` header). Security-relevant rules elevated to errors in `.editorconfig`
(CWE-89 taint `SCS0002`, weak crypto `CA5350/5351/5358/5359`, `CA2100`). CodeQL runs in CI.
The auth code adds no `unsafe`, no reflection-based type loading, no dynamic SQL.

## SCA — PASS
`dotnet list VSoftSol.Syslog.sln package --vulnerable --include-transitive` →
**"no vulnerable packages"** for all 13 projects (`sca-vulnerable.txt`).
New runtime dependencies this phase:
- `Konscious.Security.Cryptography.Argon2` 1.3.1 — pure-managed, MIT, no transitive deps.
- `System.Security.Cryptography.ProtectedData` 8.0.0 — Microsoft, DPAPI wrapper.
- `System.Text.Json` pinned to 8.0.5 (transitive pin; ahead of the 8.0.4 that carries GHSA-8g4q-xg66-9fp4).
`bunit` was evaluated for design-system component tests and **dropped**: it drags in
`AngleSharp` which carries an unfixed Moderate advisory (GHSA-pgww-w46g-26qg) with no
patched release, and SCA must stay clean. The design system is instead verified through
`DesignSystemRenderTests` (login / users / about pages render entirely from `ds-*`
components) and the full `AuthorizationMatrixTests` route render pass.

## Secrets scan — PASS
Gitleaks runs full-history in CI. No secret in source or fixtures. `SecretStoreTests`
proves the DPAPI-protected value is never the plaintext bytes and a whole-database text
dump after a secret-bearing config-change audit contains **zero** occurrences of the
plaintext.

## Security test suite — PASS
| Area | Test class | Result |
|---|---|---|
| Argon2id params + constant-time | `Argon2idPasswordHasherTests` (11) | pass |
| Lockout, enumeration timing, disabled/locked paths | `LocalAuthenticationProviderTests` (10) | pass |
| RBAC matrix (4 roles × every route × discovery) | `AuthorizationMatrixTests` (62 cases) | pass |
| Scope chokepoint / IDOR (by id, query param, sort, context) | `ScopedEventReaderTests` (7) + `UserScopeTests` (5) | pass |
| Scope architecture (Web ↮ ILogRepository) | `ScopeChokepointArchitectureTests` (2) | pass |
| Audit immutability (repo API, raw SQL) + hash chain | `AuditLogTests` (7) + `AuditDiffTests` (5) | pass |
| Session: fixation, logout invalidation, disable mid-session, CSRF | `AuthFlowTests` (8) | pass |
| Security headers + cookie flags (asserted, not inspected) | `SecurityHeadersTests` (6) | pass |
| DPAPI secret storage + leak scan | `SecretStoreTests` (6) | pass |

## ASVS L2 — V1/V2/V3/V4/V7/V14 verification pass
Recorded in `docs/security/ASVS-checklist.md`. Every applicable control in those chapters
is now **I** with a named test. The single control not executed here is **DAST**.

## DAST (OWASP ZAP) — NOT RUN (environmental) → carried to Phase 12 / CI
The build host is an SDK-only 2-vCPU VM with no Docker / WSL / browser (see the
`dev-vm-constraints` project memory; same class as the Phase 3 rsyslog oracle and the
Stryker runner). ZAP full scan against the running UI is on the Phase 12 checklist and is
wired for the CI host. Compensating controls in place now: the header/cookie/CSRF/session
assertions above are executed by xUnit against the real Kestrel pipeline via
`WebApplicationFactory` over HTTPS, and the CSP is asserted to contain no
`unsafe-inline`/`unsafe-eval` with a fresh nonce per response.

## Findings
0 Critical, 0 High, 0 Medium. 1 Low carried: **P0-3 is now CLOSED** (CSP nonces shipped).
New Low: none. See `../known-issues.md`.
