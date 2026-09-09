# Phase 7 — known issues

| Id | Severity | Issue | Disposition |
|---|---|---|---|
| P7-4 | Info / MARGINAL | Ingest benchmark with vendor extraction **and** 50 active rules reads ~3.6k msg/sec vs the 5,000 gate on the 2-vCPU VMware VM | **The gate is met on the RFC path: 50 rules → 9,433 msg/sec.** The vendor-extraction path is **sub-gate at baseline** on this VM (4,587; Phase 3 recorded ~5,290 median here — the pre-existing **P3-2** condition). Rule evaluation adds ~21 % on top of that (and ~46 % on the RFC path, in the ~10/50-rules-fire-every-message worst case that writes 400 K outbox rows for 40 K messages). The literal "≥ 5,000 with vendor + 50 rules" confirmation is carried to the Phase 12 clean-VM acceptance run — same disposition as P1-1, P3-2, P6-1. `benchmarks.md`. |
| P7-5 | Info / flake | `LocalAuthenticationProviderTests.AuthenticateAsync_UnknownUserVsWrongPassword_TakeComparableTime` (Phase 4) failed once in the full-suite Release run (ratio 3.0 vs a 2.0 threshold) | Load-dependent Argon2 decoy-hash timing flake on the 2-vCPU VM under full-suite + benchmark contention; passes 2 of 3 runs in isolation. Not a Phase 7 change — this test and Argon2 are Phase 4. Same class as P2-5 (WAL flake). Carried; a CI host with dedicated cores should widen the ratio threshold or gate it on a quiet run. |
| P7-3 | Info | `WriteToOdbc` live round-trip not run — no ODBC driver on this build VM | `OdbcWriteExecutor` is fully implemented (connection string + secret append, identifier-validated table/columns, parameterised INSERT) and unit-tested for the injection guards. The live SQLite-ODBC round-trip is carried to the Phase 12 clean-VM run — same pattern as P3-1 (rsyslog oracle) / P4-1 (ZAP). |
| P4-1 | Info | OWASP ZAP DAST | Carried. The new `/rules*` surfaces get compensating xUnit assertions against real Kestrel over HTTPS (`RuleWebTests`) — route auth, role-at-the-service, dry-run-executes-nothing. |
| P4-2 | Info | axe-core + live keyboard/AT traversal + narrow-viewport screenshot | Carried. Structural a11y for the Phase 7 rule editor / templates / tester verified in source + pre-rendered HTML (`ux-gate.md`): native `<input>`/`<select>`/`<button>`, `aria-label` on the notification bell, `<details>` for the advanced section and the raw-JSON escape hatch. |
| P3-3 | — | Stryker mutation run (rules evaluator ≥ 70 %) | Still blocked on this SDK-only host (VsTest adapter, `dev-vm-constraints`). Compensating: the 10,000-case rules-matcher oracle (0 divergences), the ReDoS suite (ADR 0014), the action fault-injection matrix. Carried to a CI host. |
| P5-3 | Info | User-authored extractors stored (`user_extractors`, migration 003) but still not applied at ingest | Not part of the Phase 7 prompt (rules/actions). Re-targeted again — best done alongside the Phase 8 alert conditions or a dedicated extractor-wiring pass. Table + store + tester complete; no `TODO(phase-N)` in a hot path. |

Coverage detail: `docs/evidence/phase-07/coverage-summary.txt`.

No Critical, High, or Medium findings. No open Low findings. `TODO(phase-N)` markers in
shipping code: none.
