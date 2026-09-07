# Phase 4 — red / green log

TESTING_STANDARDS.md §2.1: every test observed failing for the right reason before the
implementation existed. Each slice below: tests written first (or the implementation
stubbed to `throw new NotImplementedException()`), run RED, implemented, run GREEN.

| Slice | Tests | RED (observed) | GREEN |
|---|---|---|---|
| Argon2id password hasher | `Argon2idPasswordHasherTests` (11) | impl stubbed to throw — **10 failed / 1 passed** (`System.NotImplementedException`; the 1 pass is the options-only default-params check) | 11/11 pass |
| Migration 002 (auth/scope/audit schema) | `MigrationRunnerTests` (existing, count-driven) + `Migration002Tests` | new tables absent before 002 authored | applied count 1→2, all green |
| Local authentication + lockout | `LocalAuthenticationProviderTests` (10) | `AuthenticateAsync` stubbed to throw — **9 failed / 1 passed** (the pass is `RefreshAsync_DisabledUser`) | 10/10 pass |
| Audit log (append-only + hash chain) | `AuditLogTests` (7) | `AppendAsync` stubbed to throw — **6 failed / 1 passed** (the pass is the no-mutating-method reflection check) | 7/7 pass |
| Audit before/after diff + redaction | `AuditDiffTests` (5) | `AuditDiff.Snapshot` stubbed to throw — **5 failed** | 5/5 pass |
| Scope predicate | `UserScopeTests` (5) | `UserScope.Allows` → `return true` — **3 failed / 2 passed** (unrestricted + FromUser) | 5/5 pass |
| Scope chokepoint | `ScopedEventReaderTests` (7) | `ScopedEventReader` scope narrowing + `IsVisibleAsync` bypassed — **4 failed / 3 passed** | 7/7 pass |
| DPAPI secret store + leak scan | `SecretStoreTests` (6) | `SetAsync` stores plaintext (no `Protect`) — **3 failed** (ciphertext check, round-trip, leak scan) | 6/6 pass |
| Session store + cookie-auth flow | `AuthFlowTests` (8), `WebHostSmokeTests` (5) | features absent before Web auth wired; `Root_ReturnsPlaceholderPage` (Phase 0) failed once auth was required — updated to the Phase 4 redirect assertion, decision recorded in PROGRESS §"Regression" | all pass |
| RBAC authorization matrix | `AuthorizationMatrixTests` (62 cases) | `AuthPolicies.AddSyslogPolicies` stripped of `RequireRole` — **12 failed** (every deny case wrongly allowed: ReadOnly/Auditor reaching Operate/Administer/ViewAudit routes) | 62/62 pass |
| Scope chokepoint architecture | `ScopeChokepointArchitectureTests` (2) | (arch invariant — no knockout; would fail if a Web ctor took `ILogRepository`) | 2/2 pass |
| Security headers + cookie flags | `SecurityHeadersTests` (6) | Phase 0 CSP had `unsafe-inline`; `Csp_HasNoUnsafeInline` red against the Phase 0 middleware | 6/6 pass after CSP rewrite |
| Design-system reuse on real screens | `DesignSystemRenderTests` (4) | login/users pages did not exist | 4/4 pass |



