# ADR 0005 — One Windows Service hosting collector and UI, not split processes

**Status:** Accepted (Phase 0)

## Context

The product could run the collector and the web UI as two processes/services, or as one.
The UI must reflect collector state with no configuration, and setup must be trivial
(CLAUDE.md Constraint 8).

## Decision

**One composition root** (`VSoftSol.Syslog.Service`). The Windows Service host runs the
collector. The Blazor app (`VSoftSol.Syslog.Web`) is a second entry point that reuses the
exact same DI registrations (`AddSyslogPlatform`). In production packaging (Phase 12) they
run under one service; `Web` can also be launched standalone for development.

## Alternatives rejected

- **Separate collector service + separate UI service:** two things to install, start,
  secure, and keep version-matched; an IPC or shared-DB channel for the UI to read
  collector state; two service accounts or one over-privileged one.
- **UI as an IIS site:** adds an IIS dependency and a second security surface.

## Cost accepted

- A UI bug that destabilises the host process can affect ingestion. Mitigated by keeping
  ingestion on its own hosted service and background threads, bounded work, and the
  Phase 2 spill queue so in-flight messages survive a restart.
- One process to resource-limit; sizing guidance (Phase 12) accounts for both workloads.
