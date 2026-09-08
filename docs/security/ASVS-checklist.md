# ASVS-checklist.md — OWASP ASVS 5.0, Level 2

SECURITY_STANDARDS.md §1. Every applicable L2 control is `implemented`, `planned` (with
the owning phase), or `n/a` (with a reason). This is the Phase 0 baseline; each phase
updates its rows and Phase 4 does a full V1–V4/V7/V14 verification pass.

Legend: **I** implemented · **P** planned · **N/A** not applicable

| V | Area | State | Notes / owning phase |
|---|---|---|---|
| **V1** | **Architecture, design, threat modelling** | | |
| V1.1 | SDLC documents security | I | CLAUDE.md, SECURITY_STANDARDS.md, TESTING_STANDARDS.md, this checklist |
| V1.2 | Authenticated components / least privilege | P | Dedicated low-privilege service account, data-dir ACLs — ADR 0006, enforced Phase 12 |
| V1.4 | Trusted enforcement points; fail closed | I | `ScopedEventReader` is the single scope chokepoint (arch test forbids Web→`ILogRepository`); authz `FallbackPolicy` denies by default; scope resolves to "nothing" on any gap (Phase 4) |
| V1.5 | Input/output trust boundaries defined | I | THREAT_MODEL.md B1–B5 |
| V1.6 | Threat model exists and is maintained | I | THREAT_MODEL.md, reviewed Phases 4/7/11 |
| V1.11 | Business-logic limits documented | I / P | Per-source ingest rate limiter + max message size + spill size cap (Phase 2, `IngestionOptions`); rule/action budgets Phase 7 |
| V1.14 | Segregation of components | I | `Core` I/O-free (fitness test); layered projects; two seams only |
| **V2** | **Authentication** | | |
| V2.1 | Password policy, no forced composition rules, length allowed | I | `AuthenticationOptions.MinimumPasswordLength` (default 12), length-only; enforced in `AuthSessionService.ChangePasswordAsync` and `UserAdminService` (Phase 4) |
| V2.2 | Anti-automation: lockout, rate limiting | I | `SqliteUserStore.RecordLoginFailureAsync` — configurable threshold + lockout window; `LocalAuthenticationProviderTests` covers lock, correct-password-while-locked, and expiry (Phase 4) |
| V2.4 | Credential storage: Argon2id with sane params | I | `Argon2idPasswordHasher` — PHC string, OWASP defaults m=19 MiB/t=2/p=1, `FixedTimeEquals`, transparent rehash-on-login; unit-tested incl. default-param minimum (Phase 4) |
| V2.5 | Credential recovery does not reveal the current secret | I | Admin reset sets a temporary password + `must_change_password`; forced-change flow for the seeded admin; no "current password" is ever shown (Phase 4) |
| V2.7 | Constant-time verification; no user enumeration | I | Unknown user, no-password-set, and bad password each spend one Argon2 computation (decoy hash); `AuthenticationResult` reason is audit-only; `AuthenticateAsync_UnknownUserVsWrongPassword_TakeComparableTime` asserts ≤2× (Phase 4) |
| V2.x | External IdP / AD | P | `IAuthenticationProvider` seam only in v1 — ADR 0008 |
| **V3** | **Session management** | | |
| V3.1 | No session data in the URL | I | Opaque 256-bit session id in an HttpOnly cookie only; nothing session-bearing in query strings (Phase 4) |
| V3.2 | Session generated server-side, regenerated on login | I | `SqliteSessionStore.CreateAsync` mints a CSPRNG id server-side on sign-in and on password change; no client-supplied id accepted (Phase 4) |
| V3.3 | Idle and absolute timeout; logout invalidates server-side | I | `user_sessions` carries `last_seen_utc` (idle) + `absolute_expiry_utc`; `SessionCookieEvents` rejects an expired/revoked row every request; logout revokes it (`Logout_RevokesTheServerSideSession`) (Phase 4) |
| V3.4 | Cookies: `Secure`, `HttpOnly`, `SameSite` | I | Secure + HttpOnly + SameSite=Strict; `SecurityHeadersTests.AuthCookie_IsSecure_HttpOnly_AndSameSiteStrict` asserts the Set-Cookie string (Phase 4) |
| **V4** | **Access control** | | |
| V4.1 | Enforced server-side, deny by default | I | Policy-based authz with a `RequireAuthenticatedUser` `FallbackPolicy`; the route-discovery test fails the build if a page has no `[Authorize]`; the scope filter returns nothing when it cannot resolve (Phase 4) |
| V4.2 | No IDOR; object-level checks | I | `ScopedEventReader.GetByIdAsync` returns null (not "forbidden") for an out-of-scope id; `ScopedEventReaderTests` covers by-id / query-param / context-view bypass (Phase 4). Phase 5: `SqliteSavedSearchStore` / `SqliteColumnLayoutStore` re-check ownership on every mutation. Phase 6: `StreamAdminService.GetAsync/SaveAsync/ListAsync` scope-check every by-id access — an out-of-scope stream id returns the same `null` as a missing id (no existence oracle); `DeviceAdminService.ApproveAsync/RejectAsync/SaveDiscoverySettingsAsync` re-check `Role.Administrator` **at the service**, not just the page `[Authorize]` (`StreamScopeAndXssTests`, `DeviceWebTests`) |
| V4.3 | Admin interfaces need extra authz | I | `Administer` policy (Administrator only) on `/settings*`; the 4 roles x every route matrix test asserts allow/deny per `AuthPolicies.RolesFor` (Phase 4) |
| **V5** | **Validation, sanitisation, encoding** | | |
| V5.1 | Input validation with allow-lists | I / P | Schema CHECK constraints reject invalid rows at the store (Phase 1); the parser fallback chain never rejects a message (Constraint 4) — it validates the PRI range, the RFC 5424 version, and timestamp format and falls back to `raw` otherwise (Phase 3); config validation — every UI phase |
| V5.2 | Untrusted data sanitised for the sink, not on ingest | I | **No sanitisation on ingest** — `<script>`, `=cmd\|`, `../../`, `${jndi:…}` stored byte-identical, asserted (Phase 3); NUL replaced only for the SQLite-TEXT sink while `raw_message` keeps the true bytes (Constraint 4). Phase 5: `StoredXssMatrixTests` — 10 OWASP payloads stored verbatim, encoded at every render surface, CSV formula guard applied on export only. Phase 6: wire-supplied device `hostname` / `vendor` / `name` render HTML-encoded on the pending-device queue and health card, byte-identical in storage (`DeviceWebTests`, `StreamScopeAndXssTests`) |
| V5.3.4 | SQL injection prevented by parameterisation | I | `SqliteLogRepository` — CWE-89 sweep + `ToFtsPhrase` quote-doubling (Phase 1). Phase 5: `SearchCompiler` binds every user value; `SearchCompilerTests` asserts no user bytes in SQL text against injection payloads; `SearchInjectionTests` — SQL / FTS5 / unicode / 10 KB / stacked statements, no data mutated, no exception |
| V5.3 | Output encoding per context (HTML, attr, JS, CSV, PDF) | I / P | Phase 5: HTML — Razor auto-encoding on the grid / expanded row / context / live tail (`StoredXssMatrixTests`); JSON — `JavaScriptEncoder.Default` (`<>&'` escaped); CSV — RFC-4180 quoting + `CsvFormulaGuard` (`= + - @ TAB` → `'` prefix), export-only. PDF surface — Phase 10 |
| V5.3.5 | Query-language / expression injection | I | Phase 5 query language compiles to a parameterised AST → SQL; the **500-query golden-oracle differential** (`SearchOracleTests`, 0 divergences) proves the compiler is faithful. Phase 6 stream match rules compile to `CompiledCondition` (`ConditionCompiler`): unknown fields / bad values are save-time errors; `Matches` regexes use `RegexOptions.NonBacktracking` (linear-time, ReDoS-proof) + a 250 ms timeout, non-linear features rejected; the **10,000×50 routing oracle** (`StreamRoutingOracleTests`, 0 divergences) proves the evaluator is faithful; `ConditionCompilerReDoSTests` proves catastrophic patterns cannot stall ingest |
| V5.5 | Safe deserialization; no arbitrary types | I / P | `JsonExtractor` uses `System.Text.Json` with a depth cap and no polymorphic types (Phase 3); config-bundle import — Phase 11 |
| V5.2.x | ReDoS / regex safety | I | Every pack- and user-authorable pattern carries a mandatory match timeout; a timeout is caught and ingestion continues (Phase 3) |
| **V6** | **Stored cryptography** | | |
| V6.2 | Secrets encrypted at rest | I | `SqliteSecretStore` + `DpapiSecretProtector` (CurrentUser + app entropy); `SecretStoreTests` proves the stored blob is not the plaintext and the leak scan finds no plaintext in any table or audit diff (Phase 4). Consumed from Phase 7 |
| V6.4 | Key management / rotation documented | P | Phase 12 hardening guide |
| V6.x | No weak algorithms | I (gate) | `CA5350/5351/5358/5359` are build errors — `.editorconfig` |
| **V7** | **Error handling and logging** | | |
| V7.1 | No sensitive data in logs; log security events | I | Repository logs no message payloads (Phase 1); `SqliteAuditLog` records login success/failure/lockout, logout, password change/reset, user create/update, config change, secret change (`AuditActions`); `AuditDiff` redacts secret-named properties (Phase 4) |
| V7.3.1 | Logs protected from tampering | I | `audit_log` UPDATE/DELETE blocked by `BEFORE` triggers; `SqliteAuditLog` has no update/delete method (reflection-asserted) and maintains a SHA-256 hash chain — `VerifyChainAsync` detects an out-of-band row edit even with triggers dropped (`AuditLogTests`) (Phases 1 + 4) |
| V7.2 | Errors give a correlation id, not a stack trace, to users | I | `Error.razor` shows a correlation id; `DetailedErrors` only in Development |
| V7.3 | Logs protected from tampering | I | See V7.3.1 — append-only in fact (triggers + no write API) and tamper-evident (hash chain) (Phase 4) |
| V7.4 | Time source is UTC and consistent | I | All timestamps UTC; convert at UI edge only |
| **V8** | **Data protection** | | |
| V8.2 | Sensitive data not cached client-side | I | Auth pages are not cacheable; the auth cookie is session-scoped (`IsPersistent = false`); no secrets rendered to the client (Phase 4) |
| V8.3 | Least data in responses | P | Scoped queries — Phase 5 |
| **V9** | **Communications** | | |
| V9.1 | TLS everywhere for the UI | I | HTTPS-only host, HSTS configured, HTTP→HTTPS redirect (Phase 0); `A` grade target Phase 4/12 |
| V9.2 | Outbound TLS validated; no disabled cert checks | I (gate) | `CA5359` is a build error; webhook/SMTP TLS — Phase 7 |
| **V10** | **Malicious code** | | |
| V10.2 | No backdoor / debug endpoint / default credential | I / P | The seeded `admin` ships with NO password (`password_hash` NULL) and `must_change_password` = 1 — the wizard sets it, and `AuthenticateAsync_SeededAdminBeforeWizardSetsPassword_Fails` asserts it cannot log in until then (Phase 4). Full no-debug-endpoint sweep — Phase 12 |
| V10.3 | Dependency integrity; SCA; SBOM | I | Central pinned versions, no floating ranges; `dotnet list --vulnerable` clean; CycloneDX SBOM in CI; Gitleaks full history |
| **V11** | **Business logic** | | |
| V11.1 | Sequential-step and rate-limit enforcement | I / P | Per-source ingest token-bucket rate limiter with throttle / drop-with-counter / quarantine (Phase 2, tested); rule/action budgets (Phase 7) |
| **V12** | **Files and resources** | | |
| V12.1 | Upload size / type limits | P | Config-bundle import limits — Phase 11 |
| V12.3 | No user input in file paths | P (gate) | Allow-listed destinations; path traversal tests — Phases 7, 10 |
| V12.4 | Files served with correct type, no execution | I | `X-Content-Type-Options: nosniff`; static files from `wwwroot` only |
| **V13** | **API / web service** | | |
| V13.1 | Same authz for all channels | I | The Blazor circuit, SSR form components, and the `/auth/logout` minimal-API endpoint all sit behind `app.UseAuthentication()`/`UseAuthorization()` and the same policy set (Phase 4) |
| V13.2 | CSRF protection on state change | I | `app.UseAntiforgery()`; SSR forms carry `AntiforgeryToken`; `AuthFlowTests.Login_WithoutAntiforgeryToken_IsRejected` asserts a missing token gives 400 (Phase 4) |
| **V14** | **Configuration** | | |
| V14.1 | Build is repeatable and hardened | I | Deterministic + `ContinuousIntegrationBuild`; reproducibility check in CI; warnings-as-errors |
| V14.2 | No known-vulnerable dependencies | I | SCA gate; transitive pins for legacy `System.*` |
| V14.3 | No debug features in production | I | `DetailedErrors`/`AnalysisLevel` dev-only; exception handler + HSTS in non-dev |
| V14.4 | Security headers set | I | CSP with **no `unsafe-inline`/`unsafe-eval`** (per-response nonce for framework inline blocks), `object-src 'none'`, `frame-ancestors 'none'`, `base-uri`/`form-action 'self'`, HSTS, `X-Content-Type-Options`, `Referrer-Policy`, `X-Frame-Options`, COOP/CORP, `Permissions-Policy` — all asserted by `SecurityHeadersTests`, including fresh-nonce-per-response (Phase 4, closes P0-3) |
| V14.5 | HTTP method / host allow-listing | I | `AllowedHosts: localhost`; bind to localhost or a named interface |

## Phase 4 L2 verification pass (V1–V4, V7, V14)

Every V1/V2/V3/V4/V7/V14 control above that applies to the UI + auth is now **I**, each
with a named test. Verified against the running app via `AuthorizationMatrixTests`,
`AuthFlowTests`, `SecurityHeadersTests`, `ScopedEventReaderTests`, `AuditLogTests`,
`SecretStoreTests`, `LocalAuthenticationProviderTests`. DAST (OWASP ZAP) is the one
control that could not be executed on this SDK-only host — carried to the Phase 12 / CI
checklist (see `docs/evidence/phase-04/security/README.md`).

## Phase 6 L2 verification pass (V4.2, V5.2, V5.3.5)

Device registry + streams add attacker-influenced by-id objects (devices, groups, streams)
and a user-authored expression language (stream match rules). V4.2 (no IDOR / no existence
oracle, role re-checked at the service), V5.2 (device fields encoded at render, verbatim in
storage), and V5.3.5 (condition compiler faithful — 10,000×50 oracle; ReDoS-proof via
`NonBacktracking`) are **I**, each with a named test. See
`docs/evidence/phase-06/security/README.md`.

## Open L2 gaps carried out of Phase 0

- ~~CSP `'unsafe-inline'` on `style-src`~~ — **RESOLVED in Phase 4** (nonce-based CSP,
  no `unsafe-inline`, asserted by test).
- Everything still marked **P** — owned by the listed phase, re-verified there.
