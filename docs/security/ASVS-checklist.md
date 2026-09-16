# ASVS-checklist.md — OWASP ASVS 5.0, Level 2

SECURITY_STANDARDS.md §1. Every applicable L2 control is `implemented`, `planned` (with
the owning phase), or `n/a` (with a reason). This is the Phase 0 baseline; each phase
updates its rows and Phase 4 does a full V1–V4/V7/V14 verification pass.

Legend: **I** implemented · **P** planned · **N/A** not applicable

| V | Area | State | Notes / owning phase |
|---|---|---|---|
| **V1** | **Architecture, design, threat modelling** | | |
| V1.1 | SDLC documents security | I | CLAUDE.md, SECURITY_STANDARDS.md, TESTING_STANDARDS.md, this checklist |
| V1.2 | Authenticated components / least privilege | I | Dedicated low-privilege virtual service account (`NT SERVICE\VSoftSol Syslog Manager`, never LocalSystem), data-directory ACL restricted to that account plus Administrators — ADR 0006, built and structurally verified in Phase 12's installer (decompiled-MSI inspection); a live audit on an installed service is carried, `known-issues.md` |
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
| V4.2 | No IDOR; object-level checks | I | `ScopedEventReader.GetByIdAsync` returns null (not "forbidden") for an out-of-scope id; `ScopedEventReaderTests` covers by-id / query-param / context-view bypass (Phase 4). Phase 5: `SqliteSavedSearchStore` / `SqliteColumnLayoutStore` re-check ownership on every mutation. Phase 6: `StreamAdminService.GetAsync/SaveAsync/ListAsync` scope-check every by-id access — an out-of-scope stream id returns the same `null` as a missing id (no existence oracle); `DeviceAdminService.ApproveAsync/RejectAsync/SaveDiscoverySettingsAsync` re-check `Role.Administrator` **at the service**, not just the page `[Authorize]` (`StreamScopeAndXssTests`, `DeviceWebTests`). Phase 9: `SqliteDashboardStore` returns a dashboard only if owned / shared / system, `Update`/`Delete` require `owner_user_id AND NOT is_system`; `DashboardService` re-checks the role at the service; **widget aggregations run under the viewer's `UserScope` via `SearchCompiler`** so a shared dashboard's aggregate never spans out-of-scope events, and the result cache is scope-fingerprinted (`AggregationScopeTests`, `DashboardSecurityTests`, `DashboardPersistenceTests`) |
| V4.3 | Admin interfaces need extra authz | I | `Administer` policy (Administrator only) on `/settings*`; the 4 roles x every route matrix test asserts allow/deny per `AuthPolicies.RolesFor` (Phase 4) |
| **V5** | **Validation, sanitisation, encoding** | | |
| V5.1 | Input validation with allow-lists | I / P | Schema CHECK constraints reject invalid rows at the store (Phase 1); the parser fallback chain never rejects a message (Constraint 4) — it validates the PRI range, the RFC 5424 version, and timestamp format and falls back to `raw` otherwise (Phase 3); config validation — every UI phase |
| V5.2 | Untrusted data sanitised for the sink, not on ingest | I | **No sanitisation on ingest** — `<script>`, `=cmd\|`, `../../`, `${jndi:…}` stored byte-identical, asserted (Phase 3); NUL replaced only for the SQLite-TEXT sink while `raw_message` keeps the true bytes (Constraint 4). Phase 5: `StoredXssMatrixTests` — 10 OWASP payloads stored verbatim, encoded at every render surface, CSV formula guard applied on export only. Phase 6: wire-supplied device `hostname` / `vendor` / `name` render HTML-encoded on the pending-device queue and health card, byte-identical in storage (`DeviceWebTests`, `StreamScopeAndXssTests`) |
| V5.3.4 | SQL injection prevented by parameterisation | I | `SqliteLogRepository` — CWE-89 sweep + `ToFtsPhrase` quote-doubling (Phase 1). Phase 5: `SearchCompiler` binds every user value; `SearchCompilerTests` asserts no user bytes in SQL text against injection payloads; `SearchInjectionTests` — SQL / FTS5 / unicode / 10 KB / stacked statements, no data mutated, no exception |
| V5.3 | Output encoding per context (HTML, attr, JS, CSV, PDF) | I / P | Phase 5: HTML — Razor auto-encoding on the grid / expanded row / context / live tail (`StoredXssMatrixTests`); JSON — `JavaScriptEncoder.Default` (`<>&'` escaped); CSV — RFC-4180 quoting + `CsvFormulaGuard` (`= + - @ TAB` → `'` prefix), export-only. Phase 9: dashboard widget titles, dashboard names, and log-derived category labels (bar / donut / table / recent-events) render Razor-encoded, stored byte-identical — `DashboardSecurityTests` (verbatim storage) + `WidgetComponentTests` (byte-stable snapshots confirm labels are text, not markup). PDF surface — Phase 10 |
| V5.3.5 | Query-language / expression injection | I | Phase 5 query language compiles to a parameterised AST → SQL; the **500-query golden-oracle differential** (`SearchOracleTests`, 0 divergences) proves the compiler is faithful. Phase 6 stream match rules compile to `CompiledCondition` (`ConditionCompiler`): unknown fields / bad values are save-time errors; `Matches` regexes use `RegexOptions.NonBacktracking` (linear-time, ReDoS-proof) + a 250 ms timeout, non-linear features rejected; the **10,000×50 routing oracle** (`StreamRoutingOracleTests`, 0 divergences) proves the evaluator is faithful; `ConditionCompilerReDoSTests` proves catastrophic patterns cannot stall ingest. Phase 7: the `{field}` action-template engine is a literal single-field lookup (no expressions), `FieldTemplateTests`; the rules-matcher oracle (`RuleSetOracleTests`, 10,000 cases, 0 divergences) |
| V5.2.6 | SSRF defence on server-initiated requests | I | Phase 7 webhook action: `PrivateNetworkGuard` — scheme allow-list `{https,http}`; resolve the host and reject **every** resolved loopback / link-local / metadata (`169.254.0.0/16`) / RFC1918 / CGNAT / IPv6-ULA address before the request; `AllowAutoRedirect = false`; per-request timeout; bounded response read. An internal target needs a per-action opt-in **and** an admin CIDR allow-list. `WebhookSsrfTests` (13 cases) |
| V5.3.8 | OS command injection prevented | I | Phase 7 script action: `ProcessStartInfo.ArgumentList` (argument vector, `UseShellExecute = false`) — never a command string; `ExecutablePathGuard` (absolute, no `..`, symlink-resolved, allow-list); minimal scrubbed environment. `ScriptSandboxTests` |
| V5.3.9 | SMTP / IMAP injection | I | Phase 7 email action: CR/LF stripped from the templated subject and every address; recipients are config, never templated. `ActionExecutorTests.Email_SubjectCrLfInjection…` |
| V5.5 | Safe deserialization; no arbitrary types | I / P | `JsonExtractor` uses `System.Text.Json` with a depth cap and no polymorphic types (Phase 3); config-bundle import — Phase 11 |
| V5.2.x | ReDoS / regex safety | I | Every pack- and user-authorable pattern carries a mandatory match timeout; a timeout is caught and ingestion continues (Phase 3) |
| **V6** | **Stored cryptography** | | |
| V6.2 | Secrets encrypted at rest | I | `SqliteSecretStore` + `DpapiSecretProtector` (CurrentUser + app entropy); `SecretStoreTests` proves the stored blob is not the plaintext and the leak scan finds no plaintext in any table or audit diff (Phase 4). Phase 7: rule actions store only a secret **name**; the value is resolved at execute time and `ActionSecretLeakageTests` forces every failure path and greps `ActionResult.Detail` (audited) — zero hits |
| V6.4 | Key management / rotation documented | I | `docs/HARDENING_GUIDE.md` and `docs/ADMIN_GUIDE.md` document HTTPS certificate replacement; the DPAPI re-protect + `SqliteSecretStore.SetAsync` overwrite mechanism this documents already existed and is exercised by every phase that sets a secret |
| V6.x | No weak algorithms | I (gate) | `CA5350/5351/5358/5359` are build errors — `.editorconfig` |
| **V7** | **Error handling and logging** | | |
| V7.1 | No sensitive data in logs; log security events | I | Repository logs no message payloads (Phase 1); `SqliteAuditLog` records login success/failure/lockout, logout, password change/reset, user create/update, config change, secret change (`AuditActions`); `AuditDiff` redacts secret-named properties (Phase 4) |
| V7.3.1 | Logs protected from tampering | I | `audit_log` UPDATE/DELETE blocked by `BEFORE` triggers; `SqliteAuditLog` has no update/delete method (reflection-asserted) and maintains a SHA-256 hash chain — `VerifyChainAsync` detects an out-of-band row edit even with triggers dropped (`AuditLogTests`) (Phases 1 + 4) |
| V7.2 | Errors give a correlation id, not a stack trace, to users | I | `Error.razor` shows a correlation id; `DetailedErrors` only in Development |
| V7.3 | Logs protected from tampering | I | See V7.3.1 — append-only in fact (triggers + no write API) and tamper-evident (hash chain) (Phase 4) |
| V7.4 | Time source is UTC and consistent | I | All timestamps UTC; convert at UI edge only |
| **V8** | **Data protection** | | |
| V8.2 | Sensitive data not cached client-side | I | Auth pages are not cacheable; the auth cookie is session-scoped (`IsPersistent = false`); no secrets rendered to the client (Phase 4) |
| V8.3 | Least data in responses | I | Every read path (search, dashboards, reports, archives) is compiled on top of the viewer's `UserScope` — out-of-scope rows are never fetched, not filtered post-query. `AggregationScopeTests`, `DashboardSecurityTests`, `RetentionSecurityTests` (Phases 5/9/10). Closed out in the Phase 11 ASVS completion pass — the status marker was stale, the control itself has been implemented and re-verified every phase since 5 |
| **V9** | **Communications** | | |
| V9.1 | TLS everywhere for the UI | I | HTTPS-only host, HSTS configured, HTTP→HTTPS redirect (Phase 0); `A` grade target Phase 4/12 |
| V9.2 | Outbound TLS validated; no disabled cert checks | I (gate) | `CA5359` is a build error; webhook/SMTP TLS — Phase 7 |
| **V10** | **Malicious code** | | |
| V10.2 | No backdoor / debug endpoint / default credential | I | The seeded `admin` ships with NO password (`password_hash` NULL) — the first-run wizard sets it, and `AuthenticateAsync_SeededAdminBeforeWizardSetsPassword_Fails` asserts it cannot log in until then (Phase 4). Phase 12 adds `NoBackdoorTests` — a literal-string scan of the published binaries for hardcoded bypass/debug credentials — plus `AuthorizationMatrixTests`' structural proof that no route is reachable without an explicit, reviewed authorization decision |
| V10.3 | Dependency integrity; SCA; SBOM | I | Central pinned versions, no floating ranges; `dotnet list --vulnerable` clean; CycloneDX SBOM in CI; Gitleaks full history |
| **V11** | **Business logic** | | |
| V11.1 | Sequential-step and rate-limit enforcement | I / P | Per-source ingest token-bucket rate limiter with throttle / drop-with-counter / quarantine (Phase 2, tested); rule/action budgets (Phase 7) |
| **V12** | **Files and resources** | | |
| V12.1 | Upload size / type limits | I | Config bundle: `BundleValidator.MaxDocumentBytes` (100 MB) checked before any parsing; the format is pure JSON with no zip/XML container, so there is no "type" ambiguity to exploit (a file claiming to be a bundle but actually a zip/XML simply fails JSON parsing, inertly). `ConfigBundleTests.TryVerify_AnOversizedDocument_IsRefused` (Phase 11) |
| V12.3 | No user input in file paths | I | Phase 7 `WriteToFile` action: `SafeFilePath.Resolve` rejects `..` / UNC / ADS / reserved names / absolute paths; the final path must stay under a configured base dir; a `{hostname}` substitution is reduced to a safe token. `ActionExecutorTests.File_*`. Retention/report paths — Phase 10 |
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

## Phase 7 L2 verification pass (V5.2.6, V5.3.5, V5.3.8, V5.3.9, V6.2, V12.3)

Rule actions turn attacker-controllable log content into outbound HTTP / SMTP / process /
file / ODBC / syslog operations — the egress boundary (THREAT_MODEL B4). SSRF (V5.2.6),
command injection (V5.3.8), SMTP-header injection (V5.3.9), path traversal (V12.3),
template injection (V5.3.5), and secret handling on failure (V6.2) are all **I**, each with
a named test and a matrix in `docs/evidence/phase-07/security/README.md`. Threat-model
review #2 done.

## Phase 8 L2 verification pass (V4.2, V5.3.4/5, V7.1, V11.1)

Aggregation alerts (`AlertEvaluationService`) turn windows of attacker-controllable log
volume into notifications and — via the same Phase 7 executors — outbound actions.

- **V4.2 (access control at the service, not the UI)** — `AlertAdminService` checks the
  role at the service: create/edit = Administrator/Operator, delete = Administrator only,
  acknowledge/resolve refused for **Read-Only and Auditor** (not merely hidden). Instance
  listings are filtered by the caller's `UserScope`; triggering-event display routes through
  `ScopedEventReader` (the Phase 4 chokepoint) — a notification never surfaces an event from
  a stream the viewer cannot see. `AlertWebTests`, `AlertSecurityTests`.
- **V5.3.4/5 (injection / stored XSS)** — every `Data/Alerts/` SQL statement is
  parameterised; the group-by column is an allow-list lookup, never interpolated. Alert
  name/description/remediation stored byte-identical, encoded at render (Razor), and
  substituted literally by `FieldTemplate` (no expression engine). Email bodies are
  plain-text; CR/LF stripped from templated subjects (`AlertSecurityTests`). Alert *actions*
  reuse `RuleActionValidator` — the same SSRF/command/traversal/SMTP guards as a rule
  (V5.2.6 / V5.3.8 / V5.3.9 / V12.3 unchanged).
- **V7.1 (audit)** — `alert.fired` / `acknowledged` / `resolved` / `autoresolved` /
  `renotified` / `evaluation.missed` are `AuditActions` constants, written with the true
  actor into the Phase 4 SHA-256 hash-chained log. A missed scheduled run is **audited**,
  not silently skipped.
- **V11.1 (rate-limit / anti-abuse)** — per-alert dedup (one open instance per group), a
  per-alert re-notify interval, per-action rate limit + cool-down, and a global
  `Alerts:GlobalActionsPerMinute` budget with storm-collapse to one summary
  (`AlertRuntimeTests`, `AlertStormContainmentTests` — a 40k-event flood → 1 firing / 1
  notification).

Threat model **not** reviewed this phase (scheduled: Phases 4, 7, 11); new entries
B-alerts-1..4 recorded in `docs/evidence/phase-08/security/README.md` for the Phase 11
review.

## Phase 9 L2 verification pass (V4.2, V4.3, V5.3, V11.1)

Dashboards add one output surface (widgets) and one shared artifact (a shared dashboard
viewed by users with different scopes).

- **V4.2 (no IDOR, object-level checks)** — `SqliteDashboardStore.GetAsync` returns a row
  only if the caller owns it, it is shared, or it is a shipped system dashboard;
  `UpdateAsync` / `DeleteAsync` require `owner_user_id = $uid AND is_system = 0`; a forbidden
  id returns `null`, not 403 (no existence oracle). `DashboardPersistenceTests`,
  `DashboardSecurityTests.Idor_…`.
- **V4.3 (server-side, deny by default; admin scope at the service)** — routes: view =
  `ViewData`, edit = `Operate`; `DashboardService` re-checks the role at the service
  (create/edit = Administrator/Operator, delete = Administrator/Operator, copy = any
  authenticated user — default dashboards are "copyable"). **Widget aggregations run under
  the viewer's `UserScope`**: every query is parsed by the Phase 5 parser and compiled by
  `SearchCompiler`, whose scope clauses become part of the `WHERE` — a shared dashboard's
  count / sum / distinct-count aggregate can never include an out-of-scope event (a leak
  even with no row shown). The result cache key carries a `ScopeFingerprint`, so a cached
  aggregate is never served across scopes. `AggregationScopeTests`, `DashboardSecurityTests`
  (Web layer, 30 vs 7), `AggregationCacheTests`.
- **V5.3 (output encoding)** — widget titles, dashboard names, and log-derived category
  labels (hostnames / app names from the wire) render Razor-encoded; stored byte-identical
  (Constraint 4). `DashboardSecurityTests.StoredXss_…`, `WidgetComponentTests` byte-stable
  snapshots.
- **V11.1 (anti-abuse)** — a 15 s scope-keyed result cache (`Dashboards:CacheTtl`) with
  stampede protection stops a shared wall dashboard re-scanning the database per viewer;
  `TimeBucketing.MaxBuckets` (500) and `TopN` (≤ 50) cap result size.
  `AggregationCacheTests`, `DashboardConcurrencyTests` (20 concurrent → 1 query per key).

Threat model **not** reviewed this phase (scheduled: Phases 4, 7, 11); 4 dashboard entries
recorded in `docs/evidence/phase-09/security/README.md` (a B2 addendum in `THREAT_MODEL.md`)
for the Phase 11 review. No new dependency; DAST (ZAP) / axe-core NOT RUN (P4-1 / P4-2).

## Phase 10 L2 verification pass (V1.9/V1.14 file handling, V4.2, V4.3, V5.3, V12.6)

Retention adds a second file-system-resident asset (archives, alongside the SQLite file
itself) and reports add a third output surface (PDF/CSV) after search and dashboards.

- **V1.9/V1.14 (secure file handling)** — every archive read and write re-verifies path
  containment under the configured root immediately before touching the file system
  (`ArchiveNaming.IsSafeUnderRoot`); writes are atomic (temp file + rename, never a
  half-written archive at its real name); decompression is a bounded streaming copy, never
  trusting a frame's self-reported size (`BoundedCopy`) — the decompression-bomb defence.
  `ArchiveNamingTests`, `RetentionSecurityTests` (traversal + a real 50 MB bomb fixture).
- **V4.2 (no IDOR)** — `SqliteReportStore.GetAsync` returns a row only if the caller owns
  it or it is a system template; `UpdateAsync`/`DeleteAsync` require
  `owner_user_id = $uid AND is_system = 0`, no existence oracle. Archive/restore endpoints
  operate on ids only reachable through the Archives list (Administrator + Auditor via
  `ViewAudit`), never a raw user-supplied file path. `ReportStoreTests` (IDOR + system-row
  guard), `RetentionSecurityTests`.
- **V4.3 (server-side scope on every query, including restore)** — reports resolve through
  the same Phase 5/9 scoped readers as search and dashboards (no bespoke query code); a
  **scheduled** report run resolves under the **owning user's** `UserScope`, not an
  unrestricted system scope; a restored (temporarily reinstated) event's `event_streams`
  membership is rebuilt from the archive, so the existing scope chokepoint covers it exactly
  as a live row. `RetentionSecurityTests.RestoredEvents_StayScoped…`,
  `Report_ArchivedPeriodsOmitted_IsScopedToVisibleStreams…`.
- **V5.3 (output encoding, extended to PDF)** — CSV cells go through `CsvFormulaGuard` (the
  Phase 5 defence, reused); PDF values reach the page only through QuestPDF's `Text()` API,
  which draws literal glyphs — there is no markup-interpretation path for hostile log
  content to escape through. `ReportRenderingTests` (hostile content, 4 CSV-formula
  variants, all neutralised; PDF render never throws).
- **V12.6 (file upload / import handling, applied to archive restore)** — a restore
  verifies the SHA-256 **before** any decompression or parsing; a hash mismatch is refused
  outright (archive marked `tamper_detected`), never partially trusted; a malformed NDJSON
  row after a *verified* hash fails closed (refuses the whole restore, inserts nothing
  partial). `RestoreTests.RestoreArchiveAsync_WithATamperedFile_RefusesBeforeInsertingAnything`,
  `RetentionSecurityTests.RestoreArchiveAsync_ChecksTheHash_BeforeCallingArchiveFileParse`.

Threat model **not** reviewed this phase (scheduled: Phases 4, 7, 11); 5 retention/report
entries recorded in `docs/evidence/phase-10/security/README.md` (a B2/B3-adjacent addendum
in `THREAT_MODEL.md`) for the Phase 11 review. New dependencies: `ZstdSharp.Port` (pure
managed, MIT) and `QuestPDF` (Community licence) — both SCA-clean, no High/Critical.
DAST (ZAP) / axe-core NOT RUN (P4-1 / P4-2, carried).

## Phase 11 L2 verification pass (V2.1/V2.6 MFA, V4.x new listeners, V11.1, V12.1/V12.6)

- **V2.1/V2.6 (multi-factor authentication)** — RFC 6238 TOTP (`TotpGenerator`) with the
  RFC's mandated HMAC-SHA1 (an interoperability requirement, not a weak-hash finding — see
  ADR 0019 Decision 6); ten single-use recovery codes, SHA-256-hashed at rest, reuse
  rejected atomically at the store layer. `TotpGeneratorTests` (RFC 6238 Appendix B known-
  answer vector), `RecoveryCodeGeneratorTests` (reuse rejection),
  `SqliteMfaRecoveryCodeStoreTests` (`TryConsumeAsync_TheCorrectUnusedCode_SucceedsExactlyOnce`
  proves single-use at the database layer, not just in application logic). Login-flow
  enforcement is carried, not implemented — `known-issues.md` B11-3.
- **V4.x (authorization on every new page/action)** — the four new Settings/account pages
  each carry exactly one `[Authorize(Policy = ...)]`; the exhaustive authorization-matrix
  test (134 cases) passed unchanged, confirming no new page was missed.
  `AuthorizationMatrixTests`.
- **V11.1 (rate limiting)** — the Windows Event Log intake endpoint enforces a fixed-window
  per-source-IP limit independent of the ingest path's own limiter.
  `WinEventLogListenerTests.PostMoreThanTheConfiguredRateLimit_IsThrottledWithTooManyRequests`.
- **V12.1/V12.6 (upload handling, extended to config bundles)** — see V12.1 above; import
  is schema-validated and signature-verified *before* any content is processed, and never
  applies a partial import on failure (single transaction, rolled back whole).
  `ConfigBundleTests`.

## Open L2 gaps carried out of Phase 0

- ~~CSP `'unsafe-inline'` on `style-src`~~ — **RESOLVED in Phase 4** (nonce-based CSP,
  no `unsafe-inline`, asserted by test).
- Everything still marked **P** — owned by the listed phase, re-verified there.

## Phase 11 L2 completion pass (SECURITY_STANDARDS.md §1: "every ASVS L2 control that
applies is either implemented or explicitly marked not-applicable with a reason")

Every remaining **P** marker from Phase 11 was reviewed. Two were genuinely closeable then
and are **I** (V8.3, V12.1, with fresh evidence). Phase 12 closes two more, now that the
installer and documentation they depended on exist:

- **V1.2** (dedicated low-privilege service account, data-dir ACLs) — was an
  **installer-time** control (ADR 0006) that could not be verified until an installer
  existed. Phase 12 built it: **I**, structurally verified by decompiling the built MSI.
- **V6.4** (documented key rotation) — was a **documentation** deliverable. Phase 12 wrote
  it (`HARDENING_GUIDE.md`, `ADMIN_GUIDE.md`): **I**. The mechanism it documents (DPAPI
  re-protect + `SqliteSecretStore.SetAsync` overwrite) already existed and is exercised by
  every phase that sets a secret (SMTP password, SNMP community, bundle signing key, API
  keys).

One control remains **P**, deliberately, unrelated to this phase:

- **V2.x** (external IdP / AD) — **deliberately v2**. `IAuthenticationProvider` (ADR 0008)
  is one of the product's exactly two seams; a v1 that shipped a second, half-built auth
  provider would violate CLAUDE.md's "no speculative interfaces beyond these two." The seam
  exists and is exercised by the local provider; a real AD provider is future work.

With V2.x explicitly justified and every other control now closed, **every ASVS L2 control
this checklist tracks is now either implemented or has a written not-applicable-in-v1
reason** —
the Phase 11 completion requirement is met.

## v1.1 — V2.1/V2.6 MFA login-flow enforcement (B11-3 closed)

Phase 11 shipped enrollment/verification/recovery-code primitives but explicitly carried
login-flow enforcement (`known-issues.md` B11-3, restated above): an account could turn MFA
on and see no actual change in what signing in required. Closed in v1.1: `AuthSessionService.
PasswordSignInAsync` now checks `UserAccount.MfaEnabled` after a correct password and, when
set, returns `SignInStatus.MfaRequired` with a short-lived, single-use challenge
(`SqliteMfaLoginChallengeStore`, migration 010) instead of establishing a session — no
session/cookie exists until `CompleteMfaSignInAsync` verifies a current TOTP code or an
unused recovery code via the existing, already-tested `MfaSelfServiceService.
VerifyLoginCodeAsync`. A wrong-code attempt limit (`WebAuthOptions.MfaMaxAttempts`, default
5) independent of the password lockout bounds brute-forcing the 6-digit code within the
challenge's short validity window (`WebAuthOptions.MfaChallengeValidity`, default 5
minutes); exceeding it discards the challenge and forces a fresh password entry.

`MfaLoginFlowTests` (end-to-end over real HTTP, mirroring `AuthFlowTests`' static-SSR
form-post pattern): no-MFA accounts sign in exactly as before (regression-critical —
`Login_WithoutMfaEnabled_StillSignsInDirectly`); an MFA-enabled account gets no session
from password alone; a correct TOTP code (computed via `TotpGenerator` against a known
seeded secret) completes sign-in; a wrong code leaves the account unauthenticated; and
exceeding the attempt limit discards the challenge even for a subsequently-correct code.
`SqliteMfaLoginChallengeStoreTests` covers the store in isolation. Evidence:
`docs/evidence/v1.1-mfa-login-enforcement/`.

**V2.1/V2.6 is now fully I** — both the primitives (Phase 11) and login-flow enforcement
(v1.1) are implemented and tested.

## v1.1 — V5.1/V11.1 user-authored extractors wired into ingest (P5-3 closed)

Closes `docs/evidence/phase-05/known-issues.md`'s P5-3: a saved pattern from the Settings →
Pattern tester now runs against every ingested message. This moves an Operator-authored
regex from "matched only against a sample the operator pasted themselves" to "matched
against live, unauthenticated network input on the ingest hot path" — the same trust
boundary crossing already accepted for Phase 6/7's operator-authored streams and rules, not
a new one. **V5.1** (input validation): the pattern itself is untrusted-input-adjacent (it
runs against attacker-controlled syslog bodies) but the *author* is not — saving one
requires `AuthPolicies.Operate`, unchanged from Phase 5. **V11.1** (business-logic /
availability): the ReDoS risk of a catastrophic-backtracking regex is mitigated by reusing
the identical `GrokLibrary` mandatory match-timeout every vendor `.pack` file already relies
on (PHASE_03 Security Validation) — no new regex engine, no new timeout policy, no new
attack surface class. A malformed saved pattern (the store never validated regex syntax,
only the tester's live preview does) is logged and skipped at load, never fatal — the same
contract `PatternPackLoader` already gives a malformed `.pack` file. Evidence:
`docs/evidence/v1.1-user-extractor-wiring/`.

## v1.1 — V1.2/V1.14/V7.1 live listener port changes

Closes RELEASE_NOTES.md's v1.0.0 "listener port changes need a manual service restart"
limitation, UDP/TCP scope. Re-examines the Phase 12 disposition that assumed making this
live would mean "granting the web-facing service account rights to control the Windows
Service itself" (`PROGRESS.md`'s Phase 12 interpretations) — that assumption predates
nothing changing: ADR 0005/0020 already make the packaged `Web.exe` process the single
Windows Service that also holds the listener sockets (**V1.14**, segregation of
components — no new process boundary is crossed), so a port change is an in-process socket
rebind (`UdpSyslogListener`/`TcpSyslogListener.RebindAsync`), never an SCM privilege grant;
the service account's privilege stays exactly what ADR 0006 already granted it (**V1.2**).
The new socket is bound before the old one is closed, so a bind failure (port in use, no
permission) leaves the working listener untouched and this protocol is never left with zero
listeners (Constraint 3). Gated to `AuthPolicies.Administer` and every attempted change —
accepted or refused — is audited under `AuditActions.ConfigChange` (**V7.1**), the same as
every other Settings write. Evidence: `docs/evidence/v1.1-live-listener-ports/`.

## v1.1 — V11.4/V11.6 self-update check and verified download

Adds a new, opt-in, off-by-default outbound path (application → GitHub) — the first place
this product reaches the public internet at all. **This is not a control this ASVS 5.0
edition carries as its own numbered item**: 4.0.3's dedicated "V10.3 Deployed Application
Integrity Controls" chapter (auto-update must use a secure channel and be digitally signed
before installing) does not exist in 5.0 — it was folded away in the V10/V13/V15
restructuring, and the closest 5.0 controls that remain (**V15.1.2**/**V15.2.4**, supply-chain
provenance for *third-party* dependencies) are about components pulled into the build, not
about the product's own shipped-update mechanism, so citing them here would overstate the
match. Verified directly against the ASVS 5.0.0 source (`OWASP/ASVS` tag `v5.0.0`) rather than
carried forward from an older edition's numbering, per this checklist's own header
instruction.

What does apply, and is met: **V11.4.3** (hash functions used in digital signatures must be
collision-resistant, adequate bit-length) and **V11.6.1** (only approved algorithms/modes
for digital signature generation and verification) — the release-signing scheme reuses
`BundleSigner`'s existing ECDSA P-256 + SHA-256 primitive (already cited for config bundles,
ADR 0019) rather than a new algorithm choice, so this closes the same way that citation
already closed. The 4.0.3-era *intent* (signed update, secure channel, no reduced security
on downgrade) is still substantively met by construction even without a numbered 5.0 control
to point at: the release-signing tool signs a manifest with the one build-time-baked public
key (`ReleaseSigningInfo`, not TOFU — see ADR 0021 for why); `GitHubUpdateClient` requires
HTTPS in production (`GitHubUpdateOptions.RequireHttps`); a manifest that fails signature
verification is audited (`AuditActions.UpdateSignatureVerificationFailed`) and the MSI is
never downloaded; the downloaded MSI is independently re-hashed against the manifest's
pinned SHA-256 both immediately after download and again immediately before the Settings
page serves it to the Administrator (`UpdateAdminService.GetVerifiedDownloadAsync`); nothing
is ever installed automatically — the Administrator runs the already-verified MSI themselves,
elevated, so no privilege boundary is crossed by this feature (THREAT_MODEL.md B6). Evidence:
`docs/evidence/v1.1-self-update/`.

Sources checked directly: `github.com/OWASP/ASVS` at tag `v5.0.0`, chapters
`0x20-V11-Cryptography.md`, `0x22-V13-Configuration.md`, `0x24-V15-Secure-Coding-and-Architecture.md`.
