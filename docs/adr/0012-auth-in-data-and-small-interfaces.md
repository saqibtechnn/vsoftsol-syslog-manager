# ADR 0012 — Authentication lives in the Data project; small platform interfaces are allowed

**Status:** Accepted (Phase 4)

## Context

PHASE_04 builds the security model: `LocalAuthenticationProvider` (the v1 implementation
of the `IAuthenticationProvider` seam), a user store, server-side sessions, an append-only
audit log, a DPAPI secret store, and the single scoped-visibility chokepoint.

Two constraints pull against each other:

- **CLAUDE.md "Repository layout — do not invent alternatives."** The projects are fixed:
  `Core` · `Data` · `Ingestion` · `Rules` · `Reporting` · `Service` · `Web`. There is no
  `Auth` project.
- **CLAUDE.md "Two seams only"** — `ILogRepository` and `IAuthenticationProvider`. "Do not
  add speculative interfaces beyond these."

## Decision

### 1. All server-side auth infrastructure goes in `VSoftSol.Syslog.Data`.

`LocalAuthenticationProvider`, `SqliteUserStore`, `SqliteSessionStore`, `SqliteAuditLog`,
`SqliteSecretStore`, and `ScopedEventReader` are all SQLite-backed persistence / repository
code — the description of the `Data` project. `UserScope` (a pure value object with no
I/O) goes in `Core` alongside the seam types it derives from. The `Web` project keeps only
UI-framework concerns: cookie wiring, policies, the `AuthenticationStateProvider`, the SSR
form components.

Rejected: a new `VSoftSol.Syslog.Auth` project (violates the fixed layout); putting it in
`Service` (it is persistence, not composition/hosting).

### 2. Small, non-speculative platform/testability interfaces are permitted.

`IPasswordHasher` (1 real impl `Argon2idPasswordHasher` + weak-param test instances) and
`ISecretProtector` (`DpapiSecretProtector`) are added. The "two seams only" rule targets
*speculative extension points* built for a hypothetical future ("we might swap the email
provider someday"). These two are different: they are used now, on every request, and they
exist so the security tests can run without a 100 ms Argon2 hash per case and without the
Windows keyring. They are not registered as replaceable strategies and carry no config to
choose an implementation.

Everything else stays concrete: `SqliteUserStore`, `SqliteSessionStore`, `SqliteAuditLog`,
`SqliteSecretStore`, `ScopedEventReader`, `AuthSessionService`, `UserAdminService` have no
interface — tests use a real temp SQLite database, the established pattern.

### 3. The scope chokepoint is enforced by architecture test.

`ScopedEventReader` is the only event-read surface the `Web` project may use.
`ScopeChokepointArchitectureTests` fails if any `Web` type takes a constructor or field
dependency on `ILogRepository`.

## Consequences

- The seam count in spirit is unchanged: `ILogRepository` and `IAuthenticationProvider`
  remain the only *replaceable-strategy* seams. `IPasswordHasher` / `ISecretProtector` are
  documented here as deliberate, bounded exceptions.
- `Data` gains a dependency on `Konscious.Security.Cryptography.Argon2` and
  `System.Security.Cryptography.ProtectedData` — both pure libraries, no external runtime
  service, consistent with Constraint 2.
- A future AD/LDAP provider still only has to implement `IAuthenticationProvider`; it does
  not touch the UI, the policies, or the scope filter.
