# SECURITY_REVIEW.md

Running record of security findings and their disposition (SECURITY_STANDARDS.md §6).
Every accepted Medium or Low needs an operator sign-off here — not Claude Code's.

## Phase 0

| ID | Finding | Severity | Disposition | Operator sign-off |
|---|---|---|---|---|
| P0-3 | CSP allows `'unsafe-inline'` on `style-src` (Blazor error UI) | Low | Accepted for Phase 0; fixed in Phase 4 with nonces | _pending_ |

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
| Low | 1 (P0-3) | v1.0.0 (scheduled Phase 4) |

Accepted residual risks (P2-R1, P2-R2) are design properties, not defects, and do not
count against the "no open Critical/High" tag gate.
