# SECURITY_REVIEW.md

Running record of security findings and their disposition (SECURITY_STANDARDS.md §6).
Every accepted Medium or Low needs an operator sign-off here — not Claude Code's.

## Phase 0

| ID | Finding | Severity | Disposition | Operator sign-off |
|---|---|---|---|---|
| P0-3 | CSP allows `'unsafe-inline'` on `style-src` (Blazor error UI) | Low | **CLOSED in Phase 4** — CSP rewritten with a per-response nonce, no `unsafe-inline`/`unsafe-eval`; asserted by `SecurityHeadersTests` | n/a (resolved) |

## Phase 4 — environmental carry (not a finding)

| ID | Item | Severity | Disposition | Operator sign-off |
|---|---|---|---|---|
| P4-1 | OWASP ZAP DAST not executed (no Docker/browser on the build host) | Info | Carried to Phase 12 / CI. Compensating xUnit assertions run header/cookie/CSRF/session checks against the real Kestrel pipeline over HTTPS. Same class as P3-1 (rsyslog oracle). Phase 5's `/search` and `/search/export` surfaces get the same treatment. | _pending_ |
| P4-2 | axe-core a11y scan + live keyboard/AT traversal not executed (no browser) | Info | Carried to Phase 12. Structural a11y verified in source + rendered HTML (labels, roles, focus ring, native controls, `aria-live`). Extended to the Phase 5 search + pattern-tester screens (P5-2). | _pending_ |

## Phase 5 — environmental carry (not a finding)

| ID | Item | Severity | Disposition | Operator sign-off |
|---|---|---|---|---|
| P5-1 | Search-latency benchmark: 50M seed not run; broad free-text query reads MARGINAL (2.5–2.7 s vs 2 s) on the 2-vCPU VMware VM | Info | Measured against a persistent **2,000,000-event** dataset with the full p50/p95/p99 methodology + `EXPLAIN QUERY PLAN` assertions (10 shapes, all index-backed). Field-filtered queries — the realistic workflow — pass with 2–5.5× headroom (362 ms–1.0 s); a bare very-common term is I/O-bound on the cold FTS index read (BDN flags the VM). Same class as P1-1 (operator-accepted `MARGINAL`). Literal 50M + broad-free-text `< 2 s` re-verification carried to the Phase 12 clean-VM acceptance run. | _pending_ |
| P5-3 | User-authored extractors stored (`user_extractors`) but not yet applied at ingest | Info | The pattern tester saves verified patterns; wiring them into the `ExtractorPipeline` is part of the Phase 6 configuration surface. Not a security regression — nothing is auto-applied. | _pending_ |

Phase 5 security gate: **SAST PASS / SCA PASS / secrets PASS / query-injection sweep PASS
(SQL / FTS5 / unicode / 10 KB / stacked statements — parameterisation holds, no data
mutated) / golden-oracle differential PASS (500 queries, 0 divergences) / scope-bypass via
sort·wildcard·negation·export PASS / stored-XSS matrix PASS (10 OWASP payloads × grid +
JSON export, encoded at render, byte-identical in storage) / CSV formula injection PASS
(neutralised on export only, byte-identical in DB) / IDOR (saved searches + column layouts)
PASS / export DoS PASS (streamed + capped) / query-plan assertions PASS (no `SCAN events`)**.

## Phase 6 — environmental carry (not a finding)

| ID | Item | Severity | Disposition | Operator sign-off |
|---|---|---|---|---|
| P6-1 | Ingest benchmark with vendor extraction **and** 20 active streams reads MARGINAL (~3.2k vs 5k gate) on the 2-vCPU VMware VM | Info | Stream routing adds a measured **~10–12 %** to ingest throughput (20 compiled condition trees / message + `event_streams` links). The gate **is met with 20 streams on the RFC path (6,706 msg/sec)**; the vendor-extraction path is below the gate at *baseline* on this VM as well — the pre-existing **P3-2** condition — and Phase 6 adds ~10 % on top rather than causing the shortfall. Literal "≥ 5,000 with vendor extraction + 20 streams" confirmation carried to the Phase 12 clean-VM acceptance run, same carry as P1-1 and P3-2. `docs/evidence/phase-06/benchmarks.md`. | _pending_ |
| P4-1 | OWASP ZAP DAST still not executed (no Docker/browser) | Info | Carried. The new `/devices*`, `/streams*`, `/settings/discovery` surfaces get the same compensating xUnit assertions against real Kestrel over HTTPS (`DeviceWebTests`, `StreamScopeAndXssTests`) — route auth, role enforcement at the service, redirect, HTML-encoding. | _pending_ |
| P4-2 | axe-core + live keyboard/AT traversal still not executed (no browser) | Info | Carried. Structural a11y verified for the Phase 6 device/stream screens in source + pre-rendered HTML (native controls, `aria-label` on the badge and sparkline, datalist suggestions, `<details>` escape hatch). `docs/evidence/phase-06/ux-gate.md`. | _pending_ |

Phase 6 security gate: **SAST PASS / SCA PASS (no new dependency) / secrets PASS / branding
literal guard PASS / routing golden-oracle differential PASS (10,000 msgs × 50 stream
defs, 0 divergences) / ReDoS suite PASS (`NonBacktracking` linear-by-construction + 250 ms
timeout; backreferences·lookarounds·atomic-groups rejected at compile time; one bad rule
isolated, ingest never stalls) / discovery flood containment PASS (bounded at
`max_pending_devices`, drop counter, 5-min discovery pause) / discovery idempotency PASS
(1 record / 5,000 msgs from one source; 20 concurrent sources, 0 dups) / IDOR PASS
(stream Get/Save/List scope-checked, no existence oracle) / stored-XSS via device fields
PASS (hostname·vendor·name encoded on pending queue + health card, byte-identical in
storage) / authorization-on-approval PASS (Administrator-only enforced at the service, not
just the page `[Authorize]`; Operator + Read-Only refused, device stays pending)**.

## Phase 7 — environmental carry (not a finding)

| ID | Item | Severity | Disposition | Operator sign-off |
|---|---|---|---|---|
| P7-4 | Ingest benchmark with vendor extraction **and** 50 active rules reads ~3.6k msg/sec vs the 5,000 gate on the 2-vCPU VMware VM | Info | **The gate is met on the RFC path: 50 rules → 9,433 msg/sec.** The vendor-extraction path is sub-gate at *baseline* on this VM (4,587; Phase 3 recorded ~5,290 — the pre-existing **P3-2** condition). Rule evaluation adds ~21 % on top (and ~46 % on the RFC path in the ~10/50-rules-fire-every-message worst case). Literal "≥ 5,000 with vendor + 50 rules" carried to the Phase 12 clean-VM run — same disposition as P1-1, P3-2, P6-1. `docs/evidence/phase-07/benchmarks.md`. | _pending_ |
| P7-5 | `AuthenticateAsync_UnknownUserVsWrongPassword_TakeComparableTime` (Phase 4) failed once in the full-suite Release run (timing ratio 3.0 vs a 2.0 threshold) | Info | Load-dependent Argon2 decoy-hash timing flake on the 2-vCPU VM under full-suite + benchmark contention; passes 2 of 3 runs in isolation. Not a Phase 7 change (Argon2 + this test are Phase 4). Same class as P2-5. A CI host with dedicated cores should run it quiet or widen the ratio. | _pending_ |
| P7-3 | `WriteToOdbc` live round-trip not run — no ODBC driver on this build VM | Info | `OdbcWriteExecutor` fully implemented (connection string + secret append, identifier-validated table/columns, parameterised INSERT); injection guards unit-tested. Live SQLite-ODBC round-trip carried to the Phase 12 clean-VM run — same pattern as P3-1 / P4-1. | _pending_ |
| P4-1 | OWASP ZAP DAST still not executed (no Docker/browser) | Info | Carried. The new `/rules*` surfaces get compensating xUnit assertions against real Kestrel over HTTPS (`RuleWebTests`). | _pending_ |
| P4-2 | axe-core + live keyboard/AT traversal still not executed (no browser) | Info | Carried. Structural a11y for the Phase 7 rule editor / templates / tester verified in source + pre-rendered HTML. `docs/evidence/phase-07/ux-gate.md`. | _pending_ |

Phase 7 security gate: **SAST PASS / SCA PASS (one new package, `System.Data.Odbc` 8.0.1,
Microsoft-owned MIT, not vulnerable) / secrets PASS (actions store a secret *name* only;
`ActionSecretLeakageTests` forces every failure path — zero hits) / branding literal guard
PASS / SSRF matrix PASS (13 private/metadata/scheme cases refused before the request;
redirects disabled; internal target needs a per-action opt-in + an admin CIDR allow-list) /
command-injection matrix PASS (argv vector, no shell; allow-list; symlink-resolved; `..`
rejected; env not inherited) / path-traversal matrix PASS (`../`, UNC, ADS, reserved names,
`{hostname}` separators; confined to a base dir) / SMTP-header-injection PASS (CR/LF stripped,
recipients not templated) / template-injection PASS (literal field lookup, no expressions,
64 KB cap) / forward-loop protection PASS (self-target refused at save + execute, never
sent) / fault-injection matrix PASS (every action × every failure mode: contained,
classified, audited, never stalls ingestion) / ingest-isolation PASS (2,000 msgs / 0.2 s
with a blocking action) / rate-limit accuracy PASS (1,000 msgs → exactly N sends) /
idempotency PASS (outbox `UNIQUE(rule_id,event_id,action_index)`) / authorization PASS
(rule CRUD role-checked at the service; delete Administrator-only) / rules-matcher oracle
PASS (10,000 cases, 0 divergences)**. DAST (ZAP) NOT RUN (P4-1). Mutation run BLOCKED
(P3-3). Threat-model review #2 done — B4 fully re-drawn.

## Phase 2 — accepted residual risks (not findings; inherent to the design)

| ID | Risk | Severity | Disposition | Operator sign-off |
|---|---|---|---|---|
| P2-R1 | Plain UDP syslog source IPs are unverifiable / spoofable | Low (inherent) | Accepted. Mitigations: per-source rate limiter (Phase 2); source-subnet allow-list (Phase 6); mutually-authenticated TLS listener (Phase 11). Stated in the Phase 12 hardening guide. THREAT_MODEL B1. | _pending_ |
| P2-R2 | A hard `kill -9` can lose frames accepted but not yet durable — in the in-memory channel, or the ≤ `SpillFlushInterval` (default 100 ms) not-yet-fsynced tail of the spill segment | Low (inherent) | Accepted. Inherent to any non-per-message-fsync design and to unacknowledged UDP. `SpillFlushInterval` is tunable to 20 ms. A *clean* shutdown loses nothing. ADR 0010. | _pending_ |

No Critical or High findings are open.

## Open findings by severity

| Severity | Count | Must fix before |
|---|---|---|
| Critical | 0 | — |
| High | 0 | — |
| Medium | 0 | v1.0.0 |
| Low | 0 (P0-3 closed in Phase 4) | — |

Accepted residual risks (P2-R1, P2-R2) are design properties, not defects, and do not
count against the "no open Critical/High" tag gate.
