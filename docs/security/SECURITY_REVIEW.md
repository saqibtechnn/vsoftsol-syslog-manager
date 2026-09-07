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
