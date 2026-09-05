# ADR 0002 — Blazor Server over a SPA

**Status:** Accepted (Phase 0)

## Context

The UI is served from the same node as the collector, on a LAN, to a handful of
privileged admins. It must be usable with no training and no build toolchain in the
installer (CLAUDE.md Constraint 8). Every field rendered is attacker-controlled
(Constraint 9).

## Decision

**Blazor Server** (interactive server render mode), one ASP.NET Core process, sharing
the `Service` DI container.

## Alternatives rejected

- **React/Angular SPA + Web API:** a second build pipeline (npm, bundler) in the repo and
  installer, a separate auth story for the API, CORS, and client-side rendering of
  hostile log text in a much larger attack surface. No offline requirement to justify it.
- **Server-rendered MVC/Razor Pages only:** loses the live tail / streaming grid that the
  search experience needs.
- **Blazor WebAssembly:** ships a .NET runtime to the browser, larger download, and still
  needs an API tier.

## Cost accepted

- A live SignalR circuit per user; fine for the expected concurrency (single digits).
- Server holds UI state; a process restart drops circuits (acceptable for an on-prem
  admin tool).
- Latency sensitivity on the LAN is a non-issue; on a WAN it would be. The product is
  not internet-facing (Constraint 5).
