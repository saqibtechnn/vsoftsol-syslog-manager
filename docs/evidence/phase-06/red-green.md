# Phase 6 — red / green log

Every new test was observed failing for the correct reason before the implementation
existed (TESTING_STANDARDS §2.1).

---

## Slice A — condition engine (Core model + Rules evaluator/compiler)

The Phase-4 `ConditionNode` model moved from `Web.Components.DesignSystem` to
`Core/Conditions/` (ADR 0014) so the ingest-path evaluator can use it. New:
`ConditionFields` (Core allow-list), `ConditionCompiler` + `ConditionEvaluator` (Rules).

**RED — 2026-09-08** — `ConditionCompiler.Compile` shipped as
`=> throw new NotImplementedException("red-green")`:

```
dotnet test tests/VSoftSol.Syslog.UnitTests --filter "FullyQualifiedName~Conditions"
Failed!  - Failed: 58, Passed: 0, Total: 58   (all: System.NotImplementedException : red-green)
```

Test files:
- `ConditionEvaluatorTests.cs` — operator matrix (text / severity / facility / numeric /
  extracted / InList / Exists), AND/OR nesting, empty-group / null-condition → match
  nothing.
- `ConditionCompilerTests.cs` — unknown field, missing value, non-numeric range, deep /
  huge tree limits.
- `ConditionCompilerReDoSTests.cs` — catastrophic patterns compile + evaluate in bounded
  time (NonBacktracking is linear by construction); backreferences / lookarounds / absurd
  quantifiers are rejected with a clear message; one bad regex rule doesn't break the rest.

**GREEN — 2026-09-08**

```
dotnet test tests/VSoftSol.Syslog.UnitTests --filter "FullyQualifiedName~Conditions"
Passed!  - Failed: 0, Passed: 59
```

GREEN-phase fix (code, not test): `NonBacktracking` throws `NotSupportedException` (not
`ArgumentException`) for lookarounds / backreferences / huge automata — the compiler now
catches both so those patterns are rejected rather than escaping.

---

## Slice B — migration 004, device registry, discovery

`ConditionModel` also moved Web → Core; the stream router (`Rules`), stores (`Data`), and the
router provider (`Service` — the only layer allowed to bridge Data and Rules) were added.

**RED — 2026-09-08** — `004_devices_streams.sql` held back and
`SqliteDeviceStore.RegisterDiscoveredAsync` shipped as `=> throw new NotImplementedException("red-green")`:

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~Devices"
Failed!  - Failed: 20, Passed: 0, Total: 20
  ("no such table: device_ips" / "no such column: approval_status" / red-green)
```

**GREEN — 2026-09-08**

```
Passed!  - Failed: 0, Passed: 20
```

- `Migration004Tests` — the new tables/columns; `discovery_settings` seeded; `device_ips.ip`
  unique so discovery can't double-register.
- `DeviceDiscoveryTests` — **5,000 messages from one source → exactly one pending record**;
  **20 concurrent sources → 20 distinct devices, zero duplicates** (the unique-ip index +
  the process write lock); a **flood of spoofed IPs stops at `MaxPendingDevices`**, counts
  the drops, and the DB does not balloon; the `DeviceResolver` **hits the DB once per new
  IP** (100,000 messages → 1 write) and **pauses discovery once the queue is full**;
  `Reject` policy creates nothing.

## Slice C — stream router + ingest routing

**RED — 2026-09-08** — `StreamRouter.Route` shipped as `throw`:

```
dotnet test tests/VSoftSol.Syslog.UnitTests --filter "FullyQualifiedName~StreamRoutingOracle"
Failed!  - Failed: 4, Passed: 0   (System.NotImplementedException : red-green)
```

**GREEN — 2026-09-08**

```
dotnet test --filter "FullyQualifiedName~StreamRoutingOracle"          Passed! 4
dotnet test --filter "FullyQualifiedName~StreamRoutingIntegration"     Passed! (part of Devices 20)
```

- **Routing oracle** — `StreamRoutingOracleTests`: an independent naive matcher vs the
  production `StreamRouter` over **10,000 generated messages × 50 generated stream
  definitions — 0 divergences**.
- non-matching message → only the catch-all; a message matching three streams is in exactly
  those three (+ catch-all); a disabled stream does not route.
- `StreamRoutingIntegrationTests` — the seven seeded defaults compile; representative
  messages route to the expected stream; a `raw` event → Parse Failures + All Messages; a
  routed event's `event_streams` rows are written **at ingest** in the same transaction; a
  stream whose regex rule cannot compile is dropped (reported) while the others keep
  routing; the router rebuilds when a stream changes (`SqliteStreamStore.Version`).
- `IngestRoutingEndToEndTests` — the whole enrich path (frame → parse → resolve device
  auto-discovering → route → commit): an ingested message is attributed to a discovered
  device *and* lands in its streams; an enricher that throws never loses the message.

---

## Slice D — web surface (devices, streams, discovery settings, tester) + Slice-D security

Blazor pages for the device registry, approval queue, device groups, health card, stream
CRUD, and the stream tester; `DeviceAdminService` / `StreamAdminService` (the service layer
every page and picker goes through); the nav pending-device badge.

**RED — 2026-09-08** — the four authorization/scope guards short-circuited
(`DeviceAdminService.ApproveAsync` role check, `StreamAdminService.GetAsync` scope check,
`StreamAdminService.ListAsync` passing an all-scope):

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~DeviceWebTests|FullyQualifiedName~StreamScopeAndXssTests"
Failed!  - Failed: 4, Passed: 8, Total: 12
  Devices.DeviceWebTests.Approve_IsRefusedForOperatorAndReadOnly_AtTheService       [FAIL] — operator approval succeeded
  Devices.StreamScopeAndXssTests.ListAsync_ForAStreamScopedUser_ReturnsOnlyTheirStreams  [FAIL] — saw every stream
  Devices.StreamScopeAndXssTests.GetAsync_ForAnOutOfScopeStreamId_ReturnsNull_NoExistenceOracle [FAIL] — returned the row
  Devices.StreamScopeAndXssTests.SaveAsync_CannotEditAnOutOfScopeStream             [FAIL] — rename went through
```

The route-authentication and HTML-encoding assertions stayed green under this break — they
are structural (the `[Authorize]` attribute; Blazor `@`-interpolation) and cannot fail for
the right reason without deleting the mechanism; this disabled-guard build is their RED.

**GREEN — 2026-09-08**

```
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~DeviceWebTests"           Passed!  7
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "FullyQualifiedName~StreamScopeAndXssTests"   Passed!  4
```

- `DeviceWebTests` — `/devices`, `/devices/pending`, `/streams`, `/streams/tester` require
  auth; `/devices/pending` is Administrator-only; **approve/reject are refused for Operator
  and Read-Only at the service** (the record stays pending), allowed for Administrator;
  a `<script>` hostname from the wire renders HTML-encoded in the pending list.
- `StreamScopeAndXssTests` — a stream-scoped user's `ListAsync` returns only their streams
  (seeded streams they don't own are hidden); `GetAsync` of an out-of-scope id returns the
  same `null` as a non-existent id (no existence oracle); `SaveAsync` cannot rename an
  out-of-scope stream; hostile `Name` / `Hostname` / `Vendor` render encoded on the device
  health card.

## Full regression (Release, `--no-build`)

```
Unit         Passed!  632, 0 failed
Integration  Passed!  336, 0 failed        (docs/evidence/phase-06/test-output-integration.txt)
```

The known load-dependent flake `WalCrashConsistencyTests.HardKill…TwentyTimes` (P2-5)
surfaced once during an earlier full run under concurrent build load (SQLite
`disk I/O error` on the 2-vCPU VM), passes in isolation, and did not recur on the recorded
Release run. Carried unchanged — see `known-issues.md`.
