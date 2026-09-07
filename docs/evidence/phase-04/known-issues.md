# Phase 4 — known issues

| Id | Severity | Issue | Disposition |
|---|---|---|---|
| P0-3 | Low | CSP allowed `'unsafe-inline'` on `style-src` | **CLOSED** — Phase 4 ships a nonce-based CSP with no `unsafe-inline`/`unsafe-eval`, asserted by `SecurityHeadersTests`. |
| P2-1 | — | `events.listener_id` link + listener-management UI | **Partially addressed** — migration 002 keeps the FK; the listener *management screen* is Phase 6 (it is a `ComingSoon` card under Settings). Re-tagged P6. |
| P4-1 | Info | DAST (OWASP ZAP) not executed on this host | Carried to Phase 12 / CI. Compensating xUnit header/cookie/CSRF/session assertions run against the real pipeline now. |
| P4-2 | Info | axe-core automated a11y scan + live keyboard/AT traversal not executed (no browser) | Carried to Phase 12. Structural a11y (labels, roles, focus ring, keyboard-native controls, `aria-live`) verified in source + rendered HTML. |
| P4-3 | Low | `bunit` dropped from the test stack | It pulls `AngleSharp` with an unfixed Moderate advisory (GHSA-pgww-w46g-26qg); SCA must stay clean. Design-system behaviour is covered by `DesignSystemRenderTests` + the route render pass. Re-evaluate when AngleSharp ships a fix. |
| P4-4 | Info | Web line coverage 60.8% | No gate on Web. Uncovered = `ComingSoon` placeholder pages and interactive `ConditionBuilder` branches. Security-critical paths (auth flow, session lifecycle, policies, scope) are covered. Grows as later phases fill the placeholders. |
| P3-3 | — | Stryker mutation run | Still blocked on this SDK-only host; no new mutation target in Phase 4. Carried. |
| P2-5 | Info | `WalCrashConsistencyTests.HardKill…TwentyTimes` flaked once during a full-suite run, passed in isolation and on the immediate full-suite re-run | Pre-existing (Phase 2 P2-5) — a 20× hard-process-kill probe starved by parallel load on the 2-vCPU VM. Not a Phase 4 change (nothing in the WAL/ingest/spill path was touched). CI runs it on a dedicated runner. Final committed `test-output.txt` is a clean 589/589. |

No Critical, High, or Medium findings. `TODO(phase-N)` markers in shipping code: none
(the `SecurityHeadersMiddleware` Phase-0 `TODO(phase-4)` marker is removed — the work is done).
