# THREAT_MODEL.md — VSoftSol Syslog Manager

Phase 0 deliverable (SECURITY_STANDARDS.md §3). STRIDE per trust boundary. Reviewed and
updated at Phases 4, 7, and 11.

**Review log:** Phase 0 — initial. **Phase 4 — review #1 done** (B2 re-assessed end to
end now the UI/auth exist; B3 audit-tamper row updated for the hash chain; no boundary
added or removed). Next: Phase 7 (outbound actions), Phase 11 (hardening).

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
| S | Forge messages from a spoofed source IP; flood the device-discovery queue | Store `source_ip` as observed and flag it as unverified; discovery is a review queue, never auto-trust; per-source rate limiting; optional allow-list of source subnets; TLS listener gives authenticated transport | 2, 6, 11 | **partial** — per-source token-bucket rate limiter with throttle / drop-with-counter / quarantine behaviours (Phase 2); `source_ip` stored as observed; discovery queue Phase 6; TLS Phase 11 |
| T | CRLF / embedded-newline injection to fabricate or split log entries, forge a hostname | Parse framing per RFC; never split on raw newlines post-frame; store `raw_message` verbatim and render with encoding; record a `framing_anomaly` field | 3 | **implemented** — one frame → exactly one stored event (asserted); embedded CR/LF/NUL never re-parsed as a second record; `framing_anomaly` field set; `raw_message` verbatim; the wire `source_ip` always wins over a claimed hostname (asserted). `LogForgingSecurityTests`. Render-side encoding is Phases 5/9/10 |
| R | Attacker denies having sent a message | `received_utc` + `source_ip` + `listener_id` + raw bytes retained; no dedup that discards origin | 2, 3 | **partial** — `received_utc` + `source_ip` + protocol + raw bytes retained on every frame (Phase 2); `listener_id` link Phase 4; no dedup in v1 |
| I | — (listener receives, does not disclose) | n/a | | |
| D | UDP flood fills disk / exhausts the queue / OOMs the process | Bounded `Channel`; disk spill queue with a hard size cap and fail-closed drop-to-disk-full policy that still never loses an *accepted* message; per-source rate limiter; configurable max message size; back-pressure metrics surfaced in the UI | 2 | **implemented** — bounded channel + spill queue with `SpillMaxBytes` cap (drop-with-counter + alert, never fills the disk); per-source rate limiter; `MaxMessageBytes` guard; counters in `IngestionStatistics`. UI surfacing of the metrics is Phase 9. ADR 0010 |
| D | ReDoS via user-authored extractor/stream/rule regex stalling the ingest path | Compile user regex with a timeout; reject catastrophic patterns at save time with a test button; run extraction off the accept path | 3, 6 | **partial** — every pack- and (Phase 5) user-authorable pattern compiles with a mandatory match timeout (`Parsing:RegexTimeout`); a timeout is caught, counted (`extractor_timeout`), and ingestion continues (asserted, `ParserFuzzTests`). The save-time "test button" is Phase 5 |
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
| I | Stored XSS from a log payload rendered to an admin; secrets leaking into logs or audit diffs | Encode at render on every surface, never sanitise on ingest; **CSP with no `unsafe-inline` / `unsafe-eval`** (per-response nonce), `object-src 'none'`; DPAPI-encrypted secrets, `AuditDiff` redaction, name-list never returns values | 4, 5, 9, 10 | **partial** — CSP hardened + asserted, secret-leak scan clean (Phase 4). Render-side encoding of log payloads is Phases 5/9/10 (no such rendering ships in Phase 4) |
| I | CSV/formula injection when an exported CSV is opened in Excel | Prefix `= + - @` cells per OWASP; documented | 5 | planned |
| D | A malformed query or huge result set blocks the UI | Query timeout; virtualized grid (`DataTable` uses `<Virtualize>`); skeleton/partial results; background execution for > 5 s | 5 | **partial** — `DataTable` virtualization + `SkeletonLoader` shipped; query timeout / background execution Phase 5 |
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

**Assets:** internal network reachability; the service account; target systems.

| STRIDE | Threat | Mitigation | Owner | Status |
|---|---|---|---|---|
| S | Spoof a notification's origin | Signed/authenticated SMTP and webhook config; branding footer from `BrandingInfo` | 7 | planned |
| T | Path traversal via hostname/app-name reaching a file-write action or archive name | Allow-list of destination directories; sanitise path components to a safe charset; never interpolate raw fields into paths | 7, 10 | planned |
| I | SSRF via webhook action hitting cloud metadata / internal ranges | Scheme allow-list (https only by default); block link-local, loopback, and private ranges unless explicitly permitted; no redirects to disallowed hosts; DNS-rebinding guard | 7 | planned |
| D | A rule action fan-out (thousands of matches) hammers a target or the host | Per-rule rate limits and a global action budget; actions run off the accept path | 7 | planned |
| E | Command injection via script action with attacker-controlled fields | Allow-list of script paths; pass fields as arguments/stdin, never build a shell string; run as the low-privilege account; `CA3006`/`SCS0001` as build errors (Phase 0) | 7 | implemented (gate) |

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
| Phase 4 | Re-draw B2 now that the UI, RBAC, sessions, and audit log exist |
| Phase 7 | Re-draw B4 now that rule actions (SSRF, command injection, path traversal) exist |
| Phase 11 | Re-draw B1 and B5 for TLS, SNMP, Windows Event Log, and config-bundle import |
| Phase 12 | Full pre-release pen test (SECURITY_STANDARDS.md §7) |
