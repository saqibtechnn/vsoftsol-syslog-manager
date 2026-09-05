# SECURITY_REVIEW.md

Running record of security findings and their disposition (SECURITY_STANDARDS.md §6).
Every accepted Medium or Low needs an operator sign-off here — not Claude Code's.

## Phase 0

| ID | Finding | Severity | Disposition | Operator sign-off |
|---|---|---|---|---|
| P0-3 | CSP allows `'unsafe-inline'` on `style-src` (Blazor error UI) | Low | Accepted for Phase 0; fixed in Phase 4 with nonces | _pending_ |

No Critical or High findings are open.

## Open findings by severity

| Severity | Count | Must fix before |
|---|---|---|
| Critical | 0 | — |
| High | 0 | — |
| Medium | 0 | v1.0.0 |
| Low | 1 (P0-3) | v1.0.0 (scheduled Phase 4) |
