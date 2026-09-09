# THREAT_MODEL.md — VSoftSol Syslog Manager

Phase 0 deliverable (SECURITY_STANDARDS.md §3). STRIDE per trust boundary. Reviewed and
updated at Phases 4, 7, and 11.

**Review log:** Phase 0 — initial. Phase 4 — review #1 (B2 re-assessed, B3 hash chain).
**Phase 7 — review #2 done** — B4 egress boundary fully re-drawn now the rule actions exist
(SSRF, command injection, path traversal, SMTP-header injection, template injection, secret
leakage on failure, forward-loop amplification, action idempotency); all B4 rows moved from
*planned* to *implemented* with named tests. Phase 6 — B1 discovery-flood + B2 ReDoS-in-rules
row updates (no scheduled review).
**Phase 5 — review #2 done** (B2 stored-XSS row moved to **implemented** for the search
render surfaces; new B2 rows for the query language and export added). **Phase 4 — review #1 done** (B2 re-assessed end to
end now the UI/auth exist; B3 audit-tamper row updated for the hash chain; no boundary
added or removed). **Phase 6 — row status updates** (no scheduled review, no boundary
change): B1 discovery-flood spoofing and ReDoS-in-stream-rules moved from *partial* toward
*implemented* — the discovery queue is bounded and rate-paused, stream regexes compile with
`NonBacktracking` + timeout and non-linear features are rejected at save time; B2 gains a
stored-XSS-via-device-fields note (hostname/vendor encoded on the health card + pickers).
Next: Phase 7 (outbound actions), Phase 11 (hardening).

**Method:** STRIDE = Spoofing, Tampering, Repudiation, Information disclosure, Denial of
service, Elevation of privilege.

**Status legend:** `planned` (design decided, not yet built) · `partial` · `implemented`
· `accepted` (residual risk accepted, see SECURITY_REVIEW.md).

---

## System overview

Single Windows host. One service process hosts the syslog collector (UDP/TCP/TLS
listeners → bounded channel → disk spill queue → SQLite) and a Blazor Server UI over
HTTPS bound to localhost or a named LAN interface. Outbound: SMTP, HTTP webhooks, syslog
forward, ODBC, and local script execution as rule actions (Phase 7). Not internet-facing.

## Trust boundaries

| # | Boundary | Trust of the far side |
|---|---|---|
| B1 | Network → listener | Unauthenticated, hostile, spoofable (UDP) |
| B2 | Browser → web UI | Authenticated, semi-trusted (privileged admin) |
| B3 | Application → SQLite file | Trusted (same host, ACL'd directory) |
| B4 | Application → outbound actions (SMTP/webhook/script/syslog/ODBC) | Egress to attacker-influenceable targets |
| B5 | Operator → installer & config-bundle import | Privileged operator, but the *files* may be untrusted |

---

## B1 — Network → listener

**Assets:** ingestion availability; integrity of the stored record; the raw bytes.
**Entry points:** UDP 514 (+configurable), TCP 514 (+configurable), TLS listener (Phase 11).

| STRIDE | Threat | Mitigation | Owner | Status |
|---|---|---|---|---|
| S | Forge messages from a spoofed source IP; flood the device-discovery queue | Store `source_ip` as observed and flag it as unverified; discovery is a review queue, never auto-trust; per-source rate limiting; optional allow-list of source subnets; TLS listener gives authenticated transport | 2, 6, 11 | **implemented (discovery); partial (transport)** — Phase 6: discovery is a pending-approval queue, never auto-trusted; **bounded at `discovery_settings.max_pending_devices`** (default 500) with a drop counter and a 5-minute discovery pause once the queue fills, so a spoofed-IP flood cannot grow the table or starve ingest (`DeviceDiscoveryTests`); one source IP ⇒ exactly one record (`device_ips.ip` UNIQUE); unknown-source policy is operator-configurable (auto-register / as-unknown / reject). Per-source rate limiter (Phase 2). Source-subnet allow-list and TLS transport still Phase 11. |
| T | CRLF / embedded-newline injection to fabricate or split log entries, forge a hostname | Parse framing per RFC; never split on raw newlines post-frame; store `raw_message` verbatim and render with encoding; record a `framing_anomaly` field | 3 | **implemented** — one frame → exactly one stored event (asserted); embedded CR/LF/NUL never re-parsed as a second record; `framing_anomaly` field set; `raw_message` verbatim; the wire `source_ip` always wins over a claimed hostname (asserted). `LogForgingSecurityTests`. Render-side encoding is Phases 5/9/10 |
| R | Attacker denies having sent a message | `received_utc` + `source_ip` + `listener_id` + raw bytes retained; no dedup that discards origin | 2, 3 | **partial** — `received_utc` + `source_ip` + protocol + raw bytes retained on every frame (Phase 2); `listener_id` link Phase 4; no dedup in v1 |
| I | — (listener receives, does not disclose) | n/a | | |
| D | UDP flood fills disk / exhausts the queue / OOMs the process | Bounded `Channel`; disk spill queue with a hard size cap and fail-closed drop-to-disk-full policy that still never loses an *accepted* message; per-source rate limiter; configurable max message size; back-pressure metrics surfaced in the UI | 2 | **implemented** — bounded channel + spill queue with `SpillMaxBytes` cap (drop-with-counter + alert, never fills the disk); per-source rate limiter; `MaxMessageBytes` guard; counters in `IngestionStatistics`. UI surfacing of the metrics is Phase 9. ADR 0010 |
| D | ReDoS via user-authored extractor/stream/rule regex stalling the ingest path | Compile user regex with a timeout; reject catastrophic patterns at save time with a test button; run extraction off the accept path | 3, 6 | **implemented (stream rules); partial (extractors)** — Phase 6: every stream-rule `Matches` regex compiles with `RegexOptions.NonBacktracking` — **linear-time by construction, ReDoS impossible** — plus a 250 ms timeout as defence in depth; backreferences / lookarounds / atomic groups are **rejected at save time** with a clear message (the tester and the ingest router share the compiler); a per-rule evaluation timeout fails only that comparison closed and is reported, never stalling ingest or the other streams (`ConditionCompilerReDoSTests`, `StreamRoutingIntegrationTests`). Vendor-pack + user extractors still use the `Parsing:RegexTimeout` model (Phase 3/5). |
| E | Parser memory-safety / RCE from crafted input | Managed code, no unsafe parsing; fuzz corpus (CWE Top 25) in CI from Phase 3; least-privilege service account (ADR 0006) bounds impact | 3 | **implemented** — hand-written managed parser, no `unsafe`; ≥ 32k FsCheck / random cases + 1 MB / nested-SD / ANSI / NUL / truncated-PRI fuzz: zero exceptions, no hang, `raw_message` byte-identical every time; field / value / SD caps bound allocation. `ParserPropertyTests`, `ParserFuzzTests`, `MemoryBoundsSecurityTests` |

**Residual risk (accepted).** Plain UDP syslog source IPs are unverifiable and trivially
spoofable; this cannot be eliminated at the transport. Mitigations offered: the per-source
rate limiter (which bounds any single spoofed or real source), an optional source-subnet
allow-list (Phase 6), and a mutually-authenticated TLS listener (Phase 11) for customers
who need authenticated transport. A hard process kill can also lose frames that were
accepted but not yet durable (in the in-memory channel, or the ≤ `SpillFlushInterval` tail
of the spill segment) — inherent to a non-per-message-`fsync` design and to unacknowledged
UDP. Both are **accepted residual risks**, stated in the Phase 12 hardening guide, and
recorded for operator sign-off in `SECURITY_REVIEW.md`.

---

## B2 — Browser → web UI

**Assets:** admin session; configuration; the audit log; scoped log visibility.
**Entry points:** HTTPS endpoints, SignalR circuit, auth cookie, anti-forgery token.

> **Threat-model review #1 (Phase 4).** The UI and auth now exist; every B2 row below is
> re-assessed against the shipped code. Render-side XSS (grid / export / PDF) stays with
> Phases 5/9/10 — no rendering of log payloads ships in Phase 4.

| STRIDE | Threat | Mitigation | Owner | Status |
|---|---|---|---|---|
| S | Credential stuffing / brute force; session fixation; username enumeration | Argon2id (PHC, OWASP params, `FixedTimeEquals`); configurable account lockout; every failure path spends one Argon2 computation against a decoy so timing cannot distinguish unknown-user from bad-password; session id minted server-side on every login (a client-supplied id is never honoured); generic error text on the login page | 4 | **implemented** — `LocalAuthenticationProviderTests` (lockout, locked-with-correct-password, expiry, reset, timing ≤2×); `AuthFlowTests` (generic error, no cookie on failure) |
| T | CSRF; parameter/verb tampering to reach other data | `UseAntiforgery` on every state-changing request (SSR forms carry the token); policy-based authz; `ScopedEventReader` is the single query chokepoint and a caller-supplied filter can only narrow, never widen | 4, 5 | **implemented** — `AuthFlowTests.Login_WithoutAntiforgeryToken_IsRejected` (400); `ScopedEventReaderTests` (query-param / sort bypass denied) |
| R | Admin denies making a config change | Append-only audit log — actor, UTC timestamp, source IP, redacted before/after JSON; **no** update/delete method on `SqliteAuditLog` (reflection-asserted); SHA-256 hash chain detects out-of-band edits | 4 | **implemented** — `AuditLogTests` (before/after, chain intact, broken-link detection, raw UPDATE/DELETE rejected) |
| I | Stored XSS from a log payload rendered to an admin; secrets leaking into logs or audit diffs | Encode at render on every surface, never sanitise on ingest; **CSP with no `unsafe-inline` / `unsafe-eval`** (per-response nonce), `object-src 'none'`; DPAPI-encrypted secrets, `AuditDiff` redaction, name-list never returns values | 4, 5, 6, 9, 10 | **implemented (search + device surfaces)** — Phase 5: `StoredXssMatrixTests` — 10 OWASP payloads encoded on the results grid + expanded row + context view + live tail + JSON export. Phase 6: wire-supplied `hostname` / `vendor` / device `name` render HTML-encoded on the pending-device queue and the device health card, byte-identical in storage (`DeviceWebTests`, `StreamScopeAndXssTests`). Dashboard / PDF surfaces Phases 9/10 |
| E | IDOR — reach or edit another scope's stream / device / group by id; approve a device without the Administrator role | Server-side scope check on every by-id read and write (not just list filtering); no existence oracle (out-of-scope id ⇒ same response as a missing id); role re-checked in the service, not only the page `[Authorize]` | 6 | **implemented** — Phase 6: `StreamAdminService.GetAsync/SaveAsync/ListAsync` scope-checked, out-of-scope id returns the same `null` as a missing id; `DeviceAdminService.ApproveAsync/RejectAsync/SaveDiscoverySettingsAsync` require `Role.Administrator` **at the service** — Operator and Read-Only refused, device stays pending (`DeviceWebTests`, `StreamScopeAndXssTests`) |
| I | CSV/formula injection when an exported CSV is opened in Excel | Prefix `= + - @` (and TAB/CR) cells per OWASP; **export only**, DB value byte-identical | 5 | **implemented** — `CsvFormulaGuard`; `SearchExportWriterTests` + `SearchExportSecurityTests` assert both halves |
| T | Query-language injection — SQL/FTS5 fragments in the search bar reach the database, or a crafted query bypasses the scope filter | Parse to an AST → parameterised SQL (never string-built); field allow-list; the compiled `WHERE` is composed with the scope clauses; a 500-query golden-oracle differential proves the compiler is faithful | 5 | **implemented** — `SearchInjectionTests`, `SearchCompilerTests`, `SearchOracleTests` (0 divergences), `SearchScopeTests` |
| E | IDOR on saved searches / column layouts via another user's id | Every store method is keyed by the requesting user id; a shared search is readable but not editable by a non-owner | 5 | **implemented** — `SavedSearchStoreTests` |
| D | A malformed query or huge result set blocks the UI; export of the whole store | Malformed query → immediate user error, never a scan; virtualized grid; skeleton/partial results; ceiling-capped count; export streamed + `ExportMaxRows`-capped | 5 | **implemented** — `SearchQueryParserTests`, `SearchQueryPlanTests` (no `SCAN events`), `SearchExportSecurityTests` (50M-row request capped, streamed) |
| E | Vertical escalation between roles; horizontal IDOR across streams/device groups | Exhaustive authorization matrix generated from route discovery — a new route without a policy fails the test; `FallbackPolicy` requires auth; scope filter denies by default | 4, 5, 6 | **implemented** — `AuthorizationMatrixTests` (4 roles × every route × discovery + nav-coverage); `ScopeChokepointArchitectureTests` (Web cannot reference `ILogRepository`); IDOR across entities re-checked per phase as entities land |
| S | Stolen / fixated auth cookie replayed after the user logs out | The cookie bears only an opaque session id; `SessionCookieEvents` revalidates the server row every request; logout and admin action revoke it; idle + absolute timeout | 4 | **implemented** — `AuthFlowTests` (logout revokes server-side; disabled user ends next request) |

---

## B3 — Application → SQLite file

**Assets:** the event store; the audit log; FTS index.

| STRIDE | Threat | Mitigation | Owner | Status |
|---|---|---|---|---|
| T | SQL injection via any field that reaches a query | Parameterized queries only (`SqliteLogRepository` binds every value); `SCS0002` taint analysis is the enforced build-error guard, `CA2100` advisory; CWE-89 sweep test | 1 | **implemented** |
| T | Direct tampering with the DB file / audit rows | `audit_log` `UPDATE`/`DELETE` blocked by `BEFORE` triggers (`001_initial.sql`); Phase 4 adds a SHA-256 hash chain (`prev_hash`/`entry_hash`) so an edit made after dropping the triggers — or via a doctored backup — is still detectable by `VerifyChainAsync`; data-directory ACL to the service account by the installer (ADR 0006) | 1 (triggers), 4 (chain), 12 (ACL) | **partial** — triggers + hash chain in place and tested; file ACL Phase 12 |
| T | Corrupt store after a crash mid-write | WAL + `synchronous=NORMAL`; `kill -9` ×20 leaves `integrity_check = ok` and every committed row intact | 1 | **implemented** (`WalCrashConsistencyTests`) |
| I | Someone with file access reads archived data | Documented limitation; archive-at-rest encryption option (Phase 10/12) | 10, 12 | planned |
| I | Message payload leaking into internal logs | Repository logs no payloads; error paths carry no body text — asserted | 1 | **implemented** |
| D | Disk full halts writes | Spill queue + retention/tiering + disk-space alerts and self-monitoring; `wal_autocheckpoint` bounds the WAL | 2, 10, 11 | partial (WAL bound in Phase 1) |
| E | Fail-open scope filter returns all rows | Repository query filters are `IN`/`EXISTS` over the scope set — an unresolvable scope matches nothing | 1, 4 | **implemented** (Phase 1 primitive); full scope layer Phase 4 |
| R | — | WAL + backup procedure (Phase 12) | 12 | planned |

---

## B4 — Application → outbound actions

**Assets:** internal network reachability; the service account; target systems; secrets.
**Entry points:** the Phase 7 rule action list — `SendEmail`, `HttpWebhook`, `RunScript`,
`ForwardSyslog`, `WriteToFile`, `WriteToOdbc`, `RaiseNotification` (`AddTag` / `RouteToStream`
/ `Suppress` are inline, no egress).

> **Threat-model review #2 (Phase 7).** The egress boundary is now real. Every row below is
> re-assessed against the shipped executors and their guards; the matrix and the tests are
> in `docs/evidence/phase-07/security/README.md`.

| STRIDE | Threat | Mitigation | Owner | Status |
|---|---|---|---|---|
| S | SMTP header injection — CR/LF in a templated subject / address adds a header or a Bcc recipient | `FieldTemplate` strips control chars from substituted values; `EmailExecutor` additionally truncates the subject and every address at the first CR/LF; recipients are rule config, never templated | 7 | **implemented** — `ActionExecutorTests.Email_SubjectCrLfInjection…` |
| T | Path traversal via `{hostname}` / `{app}` reaching a file-write outside its directory | `SafeFilePath.Resolve` — reject `..`, UNC, ADS (`name:stream`), reserved device names, absolute paths; the final resolved path must stay under the configured base dir; a substituted value is reduced to `[A-Za-z0-9._-]` with dot-runs collapsed | 7, 10 | **implemented** — `ActionExecutorTests.File_PathTraversalAndTricks_AreRefused` (`../`, `..\`, absolute, UNC, ADS, `CON`) + `File_HostnameWithSeparators_IsSanitised` |
| I | SSRF via the webhook action reaching cloud metadata (`169.254.169.254`) / loopback / RFC1918 / CGNAT / IPv6 ULA | `PrivateNetworkGuard` — scheme allow-list `{https,http}`; resolve the host and reject **every** resolved blocked address **before** the request; `AllowAutoRedirect = false` (a 3xx is a permanent failure); per-request timeout; bounded response read. An internal target needs both a per-action opt-in flag **and** an admin-configured CIDR allow-list. DNS-rebind covered — every resolved IP is checked | 7 | **implemented** — `WebhookSsrfTests` (13 cases) + `ActionExecutorTests.Webhook_Redirect_IsRefused` |
| I | Secret leakage — a credential appears in an audit detail, a UI error, a log, or a config export | The model stores only a secret **name**; the value is resolved at execute time, held in a local, and never placed in the outbox payload, `ActionResult.Detail` (audited), a notification, or an export. `PWD=` is appended to an ODBC connection string only in-memory | 7 | **implemented** — `ActionSecretLeakageTests` (email auth-fail, ODBC connect-fail with PWD appended, missing secret) — zero hits |
| D | A rule fan-out (thousands of matches) hammers a target or the host; a slow action stalls ingestion | Per-action token-bucket rate limit + cool-down; a global per-minute outbound budget that collapses the excess into one summary notification; **all actions run off the ingest thread** via a persisted outbox drained by `ActionDispatchService` | 7 | **implemented** — `RuleRuntimeTests` (exact rate-limit counts, storm collapse) + `RuleIngestIsolationTests` (2,000 msgs / 0.2 s with a blocking action) |
| D | `ForwardSyslog` pointed at the collector's own listener — an amplification loop | The target `host:port` is compared against the collector's bound listener endpoints (injected from `IngestionOptions`); a match is refused at save time **and** at execute time; a literal loopback address on any local port is caught | 7 | **implemented** — `RuleCompilerTests.Compile_ForwardToOwnListener…` + `ActionExecutorTests.Forward_ToOwnListener_IsRefusedAsALoop_NeverSent` |
| E | Command injection via the script action with attacker-controlled fields | `ProcessStartInfo.ArgumentList` (argument vector, `UseShellExecute = false`) — never a command string; `ExecutablePathGuard` — absolute path, no `..`, symlink-resolved (`ResolveLinkTarget`) and re-checked against the allow-list; a minimal scrubbed environment (OS variables only, not the service config); `CA3006` / `SCS0001` as build errors (Phase 0) | 7 | **implemented** — `ScriptSandboxTests` (argv token, allow-list, `..`, symlink target, timeout-kill, no env inheritance) |
| E | Template injection — a `{token}` executes an expression or reaches outside the event | `FieldTemplate` is a literal single-field lookup against `ConditionFields` (+ a small render-only set) reusing `EventFieldReader`. No expressions, no method calls, no property traversal; unknown token → empty; output capped at 64 KB | 7 | **implemented** — `FieldTemplateTests` |
| — | Idempotency — a re-evaluated rule after a restart double-fires an action | Actions are written to the `rule_action_queue` outbox **in the event transaction**; a `UNIQUE (rule_id, event_id, action_index)` key makes a re-enqueue a no-op; an un-committed (crash-replayed) event gets a new id and never collides | 7 | **implemented** — `ActionOutboxTests`, `Migration005Tests` |

---

## B5 — Operator → installer & config-bundle import

**Assets:** host integrity; configuration; secrets.

| STRIDE | Threat | Mitigation | Owner | Status |
|---|---|---|---|---|
| T | Tampered installer | Signed MSI; documented hash | 12 | planned |
| I | Secrets in installer logs or the MSI | No credentials in the MSI or install logs; asserted by test | 12 | planned |
| D / E | Malicious config bundle: XXE, zip-slip, deserialization, oversized/malformed | Safe XML settings (no DTD/external entities); path-checked extraction; size limits; schema validation; no arbitrary type deserialization | 11 | planned |
| E | Installer creates world-writable paths or an over-privileged account | Installer security review; ACL audit on a clean VM (Phase 12); dedicated low-privilege account (ADR 0006) | 12 | planned |

---

## Phase 0 mitigations already in force

- Parameterized-SQL, `Process.Start`, weak-crypto, disabled-cert-validation, and
  empty-catch analyzer rules are **build errors** (`.editorconfig`, verified by the
  deliberate-failure run).
- HTTPS-only Blazor host bound to localhost by default; baseline response security
  headers (CSP with `frame-ancestors 'none'`, `X-Content-Type-Options`,
  `Referrer-Policy`, `X-Frame-Options`) — asserted by an integration test.
- `Core` does no I/O (no `System.Net`, no `System.Data`) — architecture fitness test.
- SCA clean (no High/Critical), dependencies centrally pinned, no floating ranges.
- Deterministic/reproducible builds.

## Review schedule

| When | Focus |
|---|---|
| Phase 4 | Re-draw B2 now that the UI, RBAC, sessions, and audit log exist — **done** |
| Phase 7 | Re-draw B4 now that rule actions (SSRF, command injection, path traversal) exist — **done** (2026-09-09) |
| Phase 11 | Re-draw B1 and B5 for TLS, SNMP, Windows Event Log, and config-bundle import |
| Phase 12 | Full pre-release pen test (SECURITY_STANDARDS.md §7) |
