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
**Entry points:** UDP 514 (+configurable), TCP 514 (+configurable), TLS 6514 (Phase 11),
SNMP trap 162/UDP (Phase 11), Windows Event Log intake HTTP endpoint (Phase 11).

| STRIDE | Threat | Mitigation | Owner | Status |
|---|---|---|---|---|
| S | Forge messages from a spoofed source IP; flood the device-discovery queue | Store `source_ip` as observed and flag it as unverified; discovery is a review queue, never auto-trust; per-source rate limiting; optional allow-list of source subnets; TLS listener gives authenticated transport | 2, 6, 11 | **implemented (discovery + transport)** — Phase 6: discovery is a pending-approval queue, never auto-trusted, bounded at `discovery_settings.max_pending_devices`; per-source rate limiter (Phase 2). Phase 11: `TlsSyslogListener` offers mutual TLS — `RequireClientCertificate` + an explicit trusted-thumbprint allow-list rejects an unknown or removed (the v1 revocation mechanism) client certificate before any byte reaches the parser (`TlsSyslogListenerTests.StartAsync_MutualTls_AnUntrustedClientCertificate_IsRejected`). Source-subnet allow-list remains a documented gap for plain UDP/TCP/SNMP, which stay unauthenticated by design (Constraint 9). |
| S | **SNMP trap with a forged/guessed community string** — an attacker without the real community string injects fabricated health/security events | The community string is a DPAPI-protected secret, never logged; a trap is decoded far enough to read the community, compared against the configured value, and dropped (silently, matching real SNMP agent behaviour, but counted) *before* the frame ever reaches `FrameIntake`; the default `public` value is refused outright — the listener will not accept traps at all until a real value is set | 11 | **implemented** — `SnmpTrapListenerTests.StartAsync_ATrapWithTheWrongCommunity_IsSilentlyDropped_NotIngested`, `StartAsync_NoNonDefaultCommunityConfigured_RefusesEveryTrap` |
| S | **Windows Event Log intake used to attribute a forged event to another host** — a caller with network reach but no key posts as if from a monitored server | Every request requires an API key (SHA-256-hashed at rest, shown once); a key may additionally be scoped to one source IP, checked server-side against the actual TCP peer address (never a client-supplied header) — a request from anywhere else with the same key is rejected | 11 | **implemented** — `WinEventLogListenerTests.PostWithoutAnApiKey_IsRejectedWithUnauthorized_NotIngested`, `PostWithAWrongApiKey_IsRejected`, `PostFromAnUnexpectedSource_WhenTheKeyIsSourceScoped_IsRejected_NotForged` |
| D | **Windows Event Log endpoint flooded by a caller who does hold a valid key** | A fixed-window per-source-IP rate limit (`WinEventLogOptions.MaxRequestsPerSourcePerMinute`), independent of the ingest path's own per-source limiter — the endpoint is authenticated and low-volume by nature, so it does not need token-bucket sophistication | 11 | **implemented** — `WinEventLogListenerTests.PostMoreThanTheConfiguredRateLimit_IsThrottledWithTooManyRequests` |
| T | **SNMP malformed/oversized varbind or OID crashes or hangs the receiver** | `SnmpBerReader` is a hand-bounded ASN.1 reader: every length is checked against the remaining buffer before use, OID arc count and varbind count are capped, indefinite-length BER is rejected — a malformed datagram returns `false`, never throws | 11 | **implemented** — `SnmpBerReaderTests` (truncated datagram, unsupported version, oversized OID, malformed varbind, too-many-varbinds, protocol-confused PDU tag — all rejected without throwing) |
| T | CRLF / embedded-newline injection to fabricate or split log entries, forge a hostname | Parse framing per RFC; never split on raw newlines post-frame; store `raw_message` verbatim and render with encoding; record a `framing_anomaly` field | 3 | **implemented** — one frame → exactly one stored event (asserted); embedded CR/LF/NUL never re-parsed as a second record; `framing_anomaly` field set; `raw_message` verbatim; the wire `source_ip` always wins over a claimed hostname (asserted). `LogForgingSecurityTests`. Render-side encoding is Phases 5/9/10 |
| R | Attacker denies having sent a message | `received_utc` + `source_ip` + `listener_id` + raw bytes retained; no dedup that discards origin | 2, 3 | **partial** — `received_utc` + `source_ip` + protocol + raw bytes retained on every frame (Phase 2); `listener_id` link Phase 4; no dedup in v1 |
| I | — (listener receives, does not disclose) | n/a | | |
| D | UDP flood fills disk / exhausts the queue / OOMs the process | Bounded `Channel`; disk spill queue with a hard size cap and fail-closed drop-to-disk-full policy that still never loses an *accepted* message; per-source rate limiter; configurable max message size; back-pressure metrics surfaced in the UI | 2 | **implemented** — bounded channel + spill queue with `SpillMaxBytes` cap (drop-with-counter + alert, never fills the disk); per-source rate limiter; `MaxMessageBytes` guard; counters in `IngestionStatistics`, surfaced on the Phase 11 self-monitoring page and the Phase 9 Collector Health dashboard. ADR 0010 |
| D | ReDoS via user-authored extractor/stream/rule regex stalling the ingest path | Compile user regex with a timeout; reject catastrophic patterns at save time with a test button; run extraction off the accept path | 3, 6 | **implemented (stream rules); partial (extractors)** — Phase 6: every stream-rule `Matches` regex compiles with `RegexOptions.NonBacktracking` — **linear-time by construction, ReDoS impossible** — plus a 250 ms timeout as defence in depth; backreferences / lookarounds / atomic groups are **rejected at save time** with a clear message (the tester and the ingest router share the compiler); a per-rule evaluation timeout fails only that comparison closed and is reported, never stalling ingest or the other streams (`ConditionCompilerReDoSTests`, `StreamRoutingIntegrationTests`). Vendor-pack + user extractors still use the `Parsing:RegexTimeout` model (Phase 3/5). |
| E | Parser memory-safety / RCE from crafted input | Managed code, no unsafe parsing; fuzz corpus (CWE Top 25) in CI from Phase 3; least-privilege service account (ADR 0006) bounds impact | 3 | **implemented** — hand-written managed parser, no `unsafe`; ≥ 32k FsCheck / random cases + 1 MB / nested-SD / ANSI / NUL / truncated-PRI fuzz: zero exceptions, no hang, `raw_message` byte-identical every time; field / value / SD caps bound allocation. `ParserPropertyTests`, `ParserFuzzTests`, `MemoryBoundsSecurityTests` |

**Residual risk (accepted).** Plain UDP/TCP syslog and SNMP source IPs are unverifiable
and trivially spoofable; this cannot be eliminated at the transport (Constraint 9 — the
product is universal-by-protocol at Tier 1, and SNMP has no transport-layer authentication
in v1/v2c by design). Mitigations offered: the per-source rate limiter (bounds any single
spoofed or real source), an optional source-subnet allow-list (Phase 6), the SNMP community
string (weak by protocol design, but at least not the default), and a mutually-authenticated
TLS listener for customers who need authenticated transport. Windows Event Log intake is the
one listener with real authentication (API key + optional source-IP scoping) because it is
the one protocol capable of carrying it. A hard process kill can also lose frames that were
accepted but not yet durable (in the in-memory channel, or the ≤ `SpillFlushInterval` tail
of the spill segment) — inherent to a non-per-message-`fsync` design and to unacknowledged
UDP. Listener-crash supervision (detecting an in-process listener that silently dies without
going through `StopAsync`) is not built in v1 — see `known-issues.md` B11-2. All of the
above are **accepted residual risks**, stated in the Phase 12 hardening guide, and recorded
for operator sign-off in `SECURITY_REVIEW.md`.

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
| I | Stored XSS from a log payload rendered to an admin; secrets leaking into logs or audit diffs | Encode at render on every surface, never sanitise on ingest; **CSP with no `unsafe-inline` / `unsafe-eval`** (per-response nonce), `object-src 'none'`; DPAPI-encrypted secrets, `AuditDiff` redaction, name-list never returns values | 4, 5, 6, 9, 10 | **implemented (search + device surfaces)** — Phase 5: `StoredXssMatrixTests` — 10 OWASP payloads encoded on the results grid + expanded row + context view + live tail + JSON export. Phase 6: wire-supplied `hostname` / `vendor` / device `name` render HTML-encoded on the pending-device queue and the device health card, byte-identical in storage (`DeviceWebTests`, `StreamScopeAndXssTests`). Phase 9: widget titles, dashboard names, and log-derived category labels (bar / donut / table) render HTML-encoded, stored byte-identical (`DashboardSecurityTests`, `WidgetComponentTests` snapshots). PDF surface Phase 10 |
| E | IDOR — reach or edit another scope's stream / device / group by id; approve a device without the Administrator role | Server-side scope check on every by-id read and write (not just list filtering); no existence oracle (out-of-scope id ⇒ same response as a missing id); role re-checked in the service, not only the page `[Authorize]` | 6 | **implemented** — Phase 6: `StreamAdminService.GetAsync/SaveAsync/ListAsync` scope-checked, out-of-scope id returns the same `null` as a missing id; `DeviceAdminService.ApproveAsync/RejectAsync/SaveDiscoverySettingsAsync` require `Role.Administrator` **at the service** — Operator and Read-Only refused, device stays pending (`DeviceWebTests`, `StreamScopeAndXssTests`) |
| I | CSV/formula injection when an exported CSV is opened in Excel | Prefix `= + - @` (and TAB/CR) cells per OWASP; **export only**, DB value byte-identical | 5 | **implemented** — `CsvFormulaGuard`; `SearchExportWriterTests` + `SearchExportSecurityTests` assert both halves |
| T | Query-language injection — SQL/FTS5 fragments in the search bar reach the database, or a crafted query bypasses the scope filter | Parse to an AST → parameterised SQL (never string-built); field allow-list; the compiled `WHERE` is composed with the scope clauses; a 500-query golden-oracle differential proves the compiler is faithful | 5 | **implemented** — `SearchInjectionTests`, `SearchCompilerTests`, `SearchOracleTests` (0 divergences), `SearchScopeTests` |
| E | IDOR on saved searches / column layouts via another user's id | Every store method is keyed by the requesting user id; a shared search is readable but not editable by a non-owner | 5 | **implemented** — `SavedSearchStoreTests` |
| D | A malformed query or huge result set blocks the UI; export of the whole store | Malformed query → immediate user error, never a scan; virtualized grid; skeleton/partial results; ceiling-capped count; export streamed + `ExportMaxRows`-capped | 5 | **implemented** — `SearchQueryParserTests`, `SearchQueryPlanTests` (no `SCAN events`), `SearchExportSecurityTests` (50M-row request capped, streamed) |
| E | Vertical escalation between roles; horizontal IDOR across streams/device groups | Exhaustive authorization matrix generated from route discovery — a new route without a policy fails the test; `FallbackPolicy` requires auth; scope filter denies by default | 4, 5, 6 | **implemented** — `AuthorizationMatrixTests` (4 roles × every route × discovery + nav-coverage); `ScopeChokepointArchitectureTests` (Web cannot reference `ILogRepository`); IDOR across entities re-checked per phase as entities land |
| S | Stolen / fixated auth cookie replayed after the user logs out | The cookie bears only an opaque session id; `SessionCookieEvents` revalidates the server row every request; logout and admin action revoke it; idle + absolute timeout | 4 | **implemented** — `AuthFlowTests` (logout revokes server-side; disabled user ends next request) |

> **Phase 12 addendum.** The first-run wizard (`/setup`) is a new, deliberate,
> pre-authentication surface — the only page in the product reachable with no credential at
> all. Threat rows specific to it, folded into this boundary rather than given a new one
> (it is still "browser → web UI", just before a session exists):

| STRIDE | Threat | Mitigation | Owner | Status |
|---|---|---|---|---|
| E | An attacker reaches `/setup` on an already-configured install and re-runs it to reset the admin password without a credential | `FirstRunGateMiddleware` computes "first run" from the database itself (no user account anywhere has a password set), not a client-supplied flag; once any credential exists, `/setup` immediately 302s to the dashboard regardless of how it is requested — there is no code path that re-enters the wizard once setup has completed | 12 | **implemented** — `FirstRunGateWebTests`, `FirstRunWizardTests` (setup unreachable again after completion) |
| D | Flooding `/setup` or the anonymous parts of the flow before setup completes | Same rate-limiting and request-size protections as every other endpoint (Phase 2 listener-side limits do not apply here — this is HTTP, not syslog — but Kestrel's own connection/request limits and the existing login-lockout-style Argon2 cost on the one write this flow performs bound the cost of abuse); this is a narrow, one-time window (until the admin completes setup, typically within minutes of install) rather than a standing surface | 12 | **accepted residual risk** — narrow window, no new capability an anonymous caller gains beyond what "be the first to finish the wizard" already implies (whoever completes it becomes the administrator, which is the intended behaviour for the literal first person to reach a freshly installed server on a private network — Constraint 5) |
| I | `/api/setup/first-message-status` (polled by the waiting page) leaks whether messages exist to an unauthorized caller | The endpoint requires authentication (`RequireAuthorization()`) and answers strictly within the caller's own `UserScope` via `ScopedEventReader`, identically to every other query surface — it was, in fact, caught reaching for `ILogRepository` directly during development and fixed before ever shipping (see `docs/evidence/phase-12/red-green.md`) | 12 | **implemented** — `ScopeChokepointArchitectureTests` (no `Web` type bypasses the chokepoint) |

---

## B3 — Application → SQLite file

**Assets:** the event store; the audit log; FTS index.

| STRIDE | Threat | Mitigation | Owner | Status |
|---|---|---|---|---|
| T | SQL injection via any field that reaches a query | Parameterized queries only (`SqliteLogRepository` binds every value); `SCS0002` taint analysis is the enforced build-error guard, `CA2100` advisory; CWE-89 sweep test | 1 | **implemented** |
| T | Direct tampering with the DB file / audit rows | `audit_log` `UPDATE`/`DELETE` blocked by `BEFORE` triggers (`001_initial.sql`); Phase 4 adds a SHA-256 hash chain (`prev_hash`/`entry_hash`) so an edit made after dropping the triggers — or via a doctored backup — is still detectable by `VerifyChainAsync`; data-directory ACL to the service account by the installer (ADR 0006) | 1 (triggers), 4 (chain), 12 (ACL) | **implemented** — triggers + hash chain tested since Phase 1/4; the installer's data-directory ACL (full control restricted to the service's own virtual account plus Administrators, nothing else) verified structurally correct in the decompiled MSI (`docs/evidence/phase-12/verification.md`); a live ACL audit against a real installed service is carried (`known-issues.md`) |
| T | Corrupt store after a crash mid-write | WAL + `synchronous=NORMAL`; `kill -9` ×20 leaves `integrity_check = ok` and every committed row intact | 1 | **implemented** (`WalCrashConsistencyTests`) |
| I | Someone with file access reads archived data | Documented limitation; archive-at-rest encryption option (Phase 10/12) | 10, 12 | planned |
| I | Message payload leaking into internal logs | Repository logs no payloads; error paths carry no body text — asserted | 1 | **implemented** |
| D | Disk full halts writes | Spill queue + retention/tiering + disk-space alerts and self-monitoring; `wal_autocheckpoint` bounds the WAL | 2, 10, 11 | partial (WAL bound in Phase 1) |
| E | Fail-open scope filter returns all rows | Repository query filters are `IN`/`EXISTS` over the scope set — an unresolvable scope matches nothing | 1, 4 | **implemented** (Phase 1 primitive); full scope layer Phase 4 |
| R | No backup and restore procedure, or an untested one | Documented procedure (`ADMIN_GUIDE.md` §Backup and restore): stop the service (a clean SQLite checkpoint/close), copy the data directory excluding the transient spill queue, and the mirror-image restore. The copy/restore logic was exercised against a synthetic data directory and confirmed byte-for-byte lossless; exercising it against a real, running installed service is carried alongside the installation matrix (`known-issues.md`) | 12 | **implemented** — `docs/evidence/phase-12/backup-restore.md` |

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

> **Phase 8 addendum (not a scheduled review — that is Phase 11).** Aggregation alerts add a
> second path to the same egress boundary: `AlertEvaluationService` (scheduled, off the
> ingest path) enqueues alert-triggered actions to `alert_action_queue`, drained by
> `AlertActionDispatchService` through the **same `ActionExecutorRegistry` and guards**
> (`RuleActionValidator` is shared between `RuleCompiler` and `AlertCompiler` — no drift).
> New rows for the Phase 11 review, evidence in `docs/evidence/phase-08/security/README.md`:

| STRIDE | Threat | Mitigation | Owner | Status |
|---|---|---|---|---|
| D | Notification flooding — an attacker generates unlimited matching events to bury the admin's inbox / a webhook | Per-alert dedup (partial unique index — one open instance per `(alert, group)`); a per-alert re-notify interval; per-action rate limit + cool-down; a global `Alerts:GlobalActionsPerMinute` budget that collapses the excess into one summary | 8 | **implemented** — `AlertStormContainmentTests` (40k-event flood → 1 firing / 1 notification; 20 groups → budget's worth + summary), `AlertRuntimeTests` |
| I | Information disclosure — an alert notification / history surfaces an event from a stream the viewer cannot see | Instance listings filtered by the viewer's `UserScope`; every triggering-event lookup goes through `ScopedEventReader.GetByIdAsync` (Phase 4 chokepoint); the history shows "#N (outside your visible scope)" with no body. Alert *action* recipients are the author's config (an Operator with full visibility) | 8, 11 | **implemented** — `AlertSecurityTests.TriggeringEvents_OutsideTheViewersScope_AreNotDisclosed` |
| T | Stored XSS in alert name / description / remediation / group value rendered in the notification centre, history, and email | Stored byte-identical, encoded at render (Razor); `FieldTemplate` substitutes literally (no expression engine); email bodies are plain-text, CR/LF stripped from templated subjects | 8 | **implemented** — `AlertSecurityTests` (store byte-identical, literal template render, no CR/LF in subject) |
| E | An unauthorised role acknowledges / resolves / edits an alert | Role checked **at the service** (`AlertAdminService`): create/edit = Administrator/Operator, delete = Administrator, ack/resolve refused for Read-Only **and Auditor**; every transition audited with the true actor into the hash-chained log | 8 | **implemented** — `AlertWebTests` (refusal + audit-with-actor) |
| — | A scheduled evaluation is skipped after a restart / overload → a real incident is missed | `alert_eval_runs` checkpoint survives restart; a run overdue past the grace window writes an `alert.evaluation.missed` audit row and catches up on the current window — never silently skipped; instance dedup + the outbox UNIQUE key `(instance_id, action_index, notify_seq)` prevent a double-fire on catch-up | 8 | **implemented** — `AlertRestartTests` (restart no double-fire; 40-min outage caught up + audited; 4 crash points converge to one instance) |

> **Phase 9 addendum (not a scheduled review — that is Phase 11).** Dashboards add one
> output surface (widgets) and one shared artifact (a shared dashboard viewed by users with
> different scopes) on the Browser → UI boundary (B2). No new egress. New rows for the
> Phase 11 review, evidence in `docs/evidence/phase-09/security/README.md`:

| STRIDE | Threat | Mitigation | Owner | Status |
|---|---|---|---|---|
| I | **Cross-scope aggregate leak** — a shared dashboard's widget counts / sums / distinct-counts include events the viewer cannot see (a leak even with no row shown) | Every widget aggregation is compiled on top of the Phase 5 `SearchCompiler` predicate, which bakes the viewer's `UserScope` stream/device clauses into the `WHERE` (ADR 0017) — there is no code path that aggregates out-of-scope events; the result cache key carries a `ScopeFingerprint` so a cached aggregate is never served across scopes | 9 | **implemented** — `AggregationScopeTests` (two scopes → 120 vs 45, grouped aggregate never names an out-of-scope group), `DashboardSecurityTests` (Web layer, 30 vs 7), `AggregationCacheTests` (scope-keyed) |
| I | Stored XSS in a widget title / dashboard name / category label (hostnames, app names from the wire become bar labels, donut legends, table rows) | Stored byte-identical (Constraint 4), encoded at render (Razor default on every `@value`); the visual-snapshot tests confirm the components emit labels as encoded text, never markup | 9, 10 | **implemented** — `DashboardSecurityTests.StoredXss_…` (verbatim storage), `WidgetComponentTests` (snapshots) |
| E | IDOR — read or edit another user's dashboard by id; edit or delete a shipped system dashboard | `SqliteDashboardStore` returns a row only if the caller owns it, it is shared, or it is a system dashboard; `Update`/`Delete` require `owner_user_id = $uid AND is_system = 0`; no existence oracle; role re-checked at `DashboardService` (create/edit = Administrator/Operator, copy = any authenticated user); every mutation audited | 9 | **implemented** — `DashboardPersistenceTests`, `DashboardSecurityTests.Idor_…`, `DashboardWebTests` (route 302, role-at-service, audit-with-actor) |
| D | A shared wall dashboard on a short auto-refresh re-scans the database for every viewer | 15 s scope-keyed result cache (`Dashboards:CacheTtl`); stampede protection; `TimeBucketing.MaxBuckets` and `TopN` cap result size; every aggregation is index-backed off `received_utc` | 9 | **implemented** — `AggregationCacheTests`, `DashboardConcurrencyTests` (20 concurrent → 1 query per key) |

> **Phase 10 addendum (not a scheduled review — that is Phase 11).** Retention adds a new
> asset class — cold archive files on local disk or a UNC path (B3-adjacent: the archive
> root is a second, file-system-resident store alongside the SQLite file) — and reports add
> a third widget-shaped output surface after search and dashboards. New rows for the
> Phase 11 review, evidence in `docs/evidence/phase-10/security/README.md`:

| STRIDE | Threat | Mitigation | Owner | Status |
|---|---|---|---|---|
| T | **Archive tampering** — an attacker with file-system access modifies a cold archive after export; the compliance record no longer matches what was actually collected | SHA-256 computed over the complete archive at creation, stored in the database (not beside the file); a scheduled `SqliteArchiveVerifier` re-hashes every archive on a rolling window and flags `tamper_detected`; `RestoreArchiveAsync` re-verifies before any parsing and refuses a mismatch outright. Documented residual risk: an attacker with *both* file-system and database write access could update both consistently — no software control defends against that | 10 | **implemented** — `ArchiveTamperMatrixTests` (bit-flip / truncate / swap-different-period, 3/3 detected), `RestoreTests.RestoreArchiveAsync_WithATamperedFile_RefusesBeforeInsertingAnything` |
| E | **Path traversal / zip-slip via archive naming** — a stream name feeds the archive file path | `ArchiveNaming.SanitizeSegment` strips separators and leading dots; `IsSafeUnderRoot` re-verifies full path containment immediately before every write *and* read; the archive format itself has no internal file entries (NDJSON, not zip) so there is nothing to zip-slip through | 10 | **implemented** — `ArchiveNamingTests` (unit, traversal matrix), `RetentionSecurityTests.ExportColdBatchAsync_WithAPathTraversalStreamName_NeverEscapesTheArchiveRoot` |
| D | **Decompression bomb** — a crafted or corrupted archive claims a small size but decompresses to exhaust memory/disk | Streamed, bounded-copy decompression (`BoundedCopy`) aborts once the running total exceeds a cap — never trusts a frame's self-reported size; applied uniformly to Zstd and the Gzip fallback | 10 | **implemented** — `CompressionTests.Decompress_BeyondTheCap_Throws…` (unit), `RetentionSecurityTests.ArchiveFile_Parse_RefusesADecompressionBomb…` (a real 50 MB-plaintext fixture) |
| I | **Cross-scope report leak, including through the restore path** — a report (on-demand or scheduled) includes events, aggregates, or an "archived data omitted" disclosure the viewer/owner cannot see; a restored (temporarily reinstated) event stays visible to someone outside its stream's scope | Reports resolve through the same Phase 5/9 scoped paths (`ScopedEventReader`, `SqliteAggregationReader`) — no bespoke query code; a scheduled run resolves under the **owning user's** `UserScope`, not an unrestricted system scope; the "archived periods omitted" disclosure itself is filtered to the viewer's visible streams before being shown; a restored event's `event_streams` rows are re-populated from the archive, so the existing scope chokepoint covers it identically to a live row | 10 | **implemented** — `RetentionSecurityTests.Report_ArchivedPeriodsOmitted_IsScopedToVisibleStreams…`, `RetentionSecurityTests.RestoredEvents_StayScoped…` |
| T | XSS / formula injection in a PDF or CSV report — the sixth and seventh output surfaces after the grid, context view, live tail, exports, and dashboard widgets | PDF: every value reaches the page through QuestPDF's `Text()` API, which draws literal glyphs — there is no markup interpretation path for hostile content to escape through. CSV: `CsvFormulaGuard` (the Phase 5 defence) applied to every data cell | 10 | **implemented** — `ReportRenderingTests.ReportPdfRenderer_Render_WithHostileLogContent_DoesNotThrow…`, `ReportCsvWriter_WriteAsync_NeutralisesFormulaInjection…` |

**Phase 11 review — folded in.** Per the review schedule below, this is the scheduled
full re-draw point. The Phase 8 (alert-triggered actions, folded into B4), Phase 9
(dashboard cross-scope/XSS/IDOR/DoS rows, above), and Phase 10 (archive/report rows,
directly above) addenda are hereby accepted as the permanent record for their boundaries —
every row above is "implemented" with committed test evidence, none is deferred, so they
stay in place rather than being physically re-transcribed into new tables for this review.
B1 and B5 (this document) are the two boundaries Phase 11 actually changes; both are
redrawn below/above.

---

## B5 — Operator → installer & config-bundle import

**Assets:** host integrity; configuration; secrets; the receiving install's rules, streams,
devices, users, dashboards, reports, and vendor parser packs.

| STRIDE | Threat | Mitigation | Owner | Status |
|---|---|---|---|---|
| D / E | **XXE, zip-slip, deserialization via a malicious config bundle** | The bundle format is pure JSON — one header plus one JSON array per section, signature detached — with **no XML and no zip container anywhere in the format**. Neither attack class can occur; not because input is sanitised, but because the mechanism each attack needs (DTD/external-entity processing, a zip central-directory path) does not exist to exploit — the same "provably absent by construction" design as the Phase 10 archive format. `System.Text.Json` (used for the whole document) has no entity-expansion concept, so billion-laughs is equally moot. Import never instantiates an arbitrary .NET type from bundle content — every write goes through a hand-written importer method with a fixed, hard-coded column list; a bundle's own JSON keys are read as data only, never as a type name, member name, or SQL identifier | 11 | **implemented** — `ConfigBundleTests` (round trip; a "bundle" cannot carry a zip or XML payload by construction — there is nothing in the parser that would interpret one even if the JSON `content` field of an extractor entry contained an XXE/zip-slip payload as inert text) |
| T | **Malformed or forged signature; a bundle signed by an untrusted key** | ECDSA P-256 signature over the exact document bytes, verified before any content is touched; the signer's public-key fingerprint must be in `bundle_trusted_signers` (trust-on-first-use, Administrator-accepted) or the import is refused outright — no partial application | 11 | **implemented** — `ConfigBundleTests.ApplyAsync_ATamperedDocument_FailsSignatureVerification`, `ApplyAsync_AnUntrustedSigner_IsRefused_BeforeAnyContentIsProcessed`, `TryVerify_AMalformedSignature_IsRefused` |
| D | **Oversized bundle** | A hard byte-size cap (`BundleValidator.MaxDocumentBytes`, 100 MB) checked before any JSON parsing is attempted | 11 | **implemented** — `ConfigBundleTests.TryVerify_AnOversizedDocument_IsRefused` |
| E | **Path traversal via a vendor-pack file name/vendor folder in the `extractors` section** | File names are sanitised with the same `ArchiveNaming.SanitizeSegment`/`IsSafeUnderRoot` the Phase 10 archive format uses, then the resolved path is re-verified to stay under the patterns root immediately before every write | 11 | **implemented** — `ConfigBundleTests.ImportExtractors_APathTraversalFileName_NeverEscapesThePatternsRoot` |
| I | **Password hashes or other secrets leak through an export** | `users.password_hash` is the one hand-maintained column exclusion in the (otherwise generic, `SELECT *`-driven) exporter; no secret-store value (SMTP password, SNMP community, API keys, MFA secrets) is ever placed in a section — only non-secret metadata is exported for anything backed by a secret | 11 | **implemented** — `ConfigBundleExporter`'s `TableSections` exclusion list; no test currently asserts the *absence* of a secret column across every future table by construction — recorded as a standing review item for any new exportable table (`known-issues.md`) |
| T | Tampered installer | Documented SHA-256 hash of the built MSI, published alongside the release, so a recipient can verify the file they received against the one this project actually built. **Authenticode code-signing is carried** — no code-signing certificate is available in this build environment; a self-published hash is the fallback integrity check for this release, not a substitute for signing on a future one | 12 | **partial** — hash published (`docs/evidence/phase-12/verification.md`); signing carried |
| I | Secrets in installer logs or the MSI | No credential, connection string, or secret-store value is embedded in the MSI or referenced by any installer property/registry value — confirmed by decompiling the built MSI's full table set and by inspection of every `RegistryValue`/`Property` this project's own `Product.wxs` defines (none carry anything beyond product identity strings and the data-directory path) | 12 | **implemented** — `docs/evidence/phase-12/verification.md` (decompiled-MSI inspection) |
| E | Installer creates world-writable paths or an over-privileged account | The service runs as a Windows **virtual account** (`NT SERVICE\VSoftSol Syslog Manager`), never LocalSystem (ADR 0006); the data directory's ACL grants full control to exactly that account plus Administrators and nothing else (`util:PermissionEx`, confirmed in the decompiled MSI's `Wix4SecureObject` table); Program Files retains Windows' own default (non-world-writable) ACL, untouched by the installer. A live ACL audit on a clean, freshly-installed VM is carried (`known-issues.md`) — this environment has no clean VM to install onto — but the *intended* ACL grant was verified structurally correct before ever being packaged | 12 | **partial** — structure verified; live-install audit carried |

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
| Phase 8 | (not a scheduled review) B4 addendum for alert-triggered actions + 4 alert-specific rows recorded for the Phase 11 review — **done** (2026-09-10) |
| Phase 9 | (not a scheduled review) B2 addendum for dashboards — cross-scope aggregate leak, stored XSS on the widget surface, dashboard IDOR, wall-dashboard DoS; 4 rows recorded for the Phase 11 review — **done** (2026-09-11) |
| Phase 10 | (not a scheduled review) archive tampering, path traversal / zip-slip in archive naming, decompression bomb, cross-scope report leak (incl. the restore path), PDF/CSV injection; 5 rows recorded for the Phase 11 review — **done** (2026-09-11) |
| Phase 11 | Re-draw B1 and B5 for TLS, SNMP, Windows Event Log, and config-bundle import; fold in the Phase 8 alert rows, the Phase 9 dashboard rows, and the Phase 10 retention/report rows — **done** (2026-09-11) |
| Phase 12 | Full pre-release pen test (SECURITY_STANDARDS.md §7) — **partial**: a self-review against the packaged build plus a no-backdoor automated assertion were performed (`docs/security/PENTEST_REPORT.md`); an independent external penetration test needs a tester and infrastructure this environment does not have, and is carried (`known-issues.md`). Installer/B5 and the new `/setup` pre-auth surface (B2 addendum) reviewed — **done** (2026-09-12) |
