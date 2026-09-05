# ADR 0008 — Exactly two seams: ILogRepository and IAuthenticationProvider

**Status:** Accepted (Phase 0)

## Context

CLAUDE.md "Two seams only": `ILogRepository` (for a future PostgreSQL) and
`IAuthenticationProvider` (for a future Active Directory). No speculative interfaces
beyond these. "Boring code wins."

## Decision

`VSoftSol.Syslog.Core.Abstractions` contains exactly these two interfaces plus their
directly-required DTOs (`LogQuery`, `AuthenticationResult`, `AuthenticatedUser`,
`AuthenticationFailureReason`). Both are fully specified in Phase 0 and unimplemented;
implementations land in Phase 1 (`SqliteLogRepository`) and Phase 4
(`LocalAuthenticationProvider`).

## Alternatives rejected

- **An interface per collaborator (`IListener`, `IParser`, `IRuleAction`, …) from day
  one:** premature abstraction; each becomes real when its phase needs it, shaped by that
  phase's requirements rather than a guess.
- **No seams, refactor when needed:** the two named seams have concrete, funded future
  drivers (PostgreSQL, AD) and touch every layer, so retrofitting them later is the
  expensive path.

## Cost accepted

- `ILogRepository` currently defines methods (context view, batch append) whose SQLite
  implementation is still ahead; the contract may gain one or two members in Phase 1/5,
  recorded here and in PROGRESS.md if so.
- New cross-cutting needs must be justified against this ADR before an interface is added.
