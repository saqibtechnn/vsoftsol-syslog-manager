# Phase 6 — security evidence

Per `SECURITY_STANDARDS.md` and the PHASE_06 "Security Validation" section.

## Gates

| Gate | Result | Evidence |
|---|---|---|
| SAST (analyzers + Roslynator as errors) | PASS | `dotnet build -c Release` warning-clean, 13 projects |
| SCA (`dotnet list package --vulnerable --include-transitive`) | PASS | `sca-vulnerable.txt` — no vulnerable packages; **no new dependency** (the condition compiler uses BCL `System.Text.RegularExpressions`; routing is hand-written) |
| Secrets scan | PASS | no secrets in source; device/stream fields and audit details never carry secret material |
| Branding literal guard | PASS | grep for brand **values** under `src/` returns only `BrandingInfo.g.cs` |

## PHASE_06 security validation

| Item | How verified | Result |
|---|---|---|
| **Discovery flood** — spoofed source IPs | `DeviceDiscoveryTests` — a flood of distinct spoofed IPs stops creating records at `discovery_settings.max_pending_devices` (default 500), `SqliteDeviceStore.FloodDropCount` counts the drops, the `devices` table does not balloon, and `DeviceResolver` enters a 5-minute discovery pause (`FloodEventCount`) so ingestion stays responsive. `discovery_settings` is operator-tunable. | PASS |
| **Discovery idempotency** — one source, many messages | `DeviceDiscoveryTests` — 5,000 messages from one unknown IP → exactly **1** pending record (`device_ips.ip` UNIQUE + `INSERT OR IGNORE` under the write lock); `DeviceResolver` caches IP→id and hits the DB **once per new IP** (100,000 resolutions → 1 write). | PASS |
| **Discovery race** — 20 concurrent sources | `DeviceDiscoveryTests` — 20 distinct sources registered concurrently → 20 devices, **0 duplicates, 0 lost registrations**. | PASS |
| **ReDoS in stream rules** — catastrophic patterns | `ConditionCompilerReDoSTests` — every `Matches` operator compiles with `RegexOptions.NonBacktracking` (linear-time **by construction**) + a 250 ms timeout as defence in depth. Known catastrophic patterns (`(a+)+$`, `(a*)*`, nested quantifiers) compile and evaluate in bounded time; backreferences / lookarounds / atomic groups are **rejected at compile time** with a "must be linear-time" message. A regex that times out at evaluation makes only that one comparison false (fail closed) — reported via `EvaluationResult.RegexTimedOut`. | PASS |
| **ReDoS isolation** — one bad rule cannot stall ingest for other devices | `StreamRoutingIntegrationTests` / `StreamRouter.Route` — each stream is evaluated in its own `try/catch`; a stream whose rule will not compile is dropped and reported via `CompileErrors`, the rest keep routing; `StreamRouterProvider` logs compile errors once per rebuild. Ingestion never sees a routing exception. | PASS |
| **IDOR** — device / group / stream by id across scopes | `StreamScopeAndXssTests` — `StreamAdminService.GetAsync` of an out-of-scope stream id returns the **same `null`** as a non-existent id (no existence oracle); `SaveAsync` refuses to edit an out-of-scope stream; `ListAsync` (which backs every stream picker) returns only in-scope streams. Device approval/rejection is re-checked at the service (below). | PASS |
| **Stored XSS via device fields** — hostname, vendor from the wire | `DeviceWebTests.HostileDeviceHostname_RendersEncodedInTheDeviceLists` (pending queue) + `StreamScopeAndXssTests.DeviceDetailHealthCard_RendersHostileDeviceFieldsEncoded` (`Name` / `Hostname` / `Vendor` on the health card) — the `<script>` payload is present as HTML-encoded text, never as a live tag. Blazor `@`-interpolation encodes every field; `raw_message` and the stored fields are unchanged (encode-at-render, Constraint 9). | PASS |
| **Authorization on approval** — Administrator only, at the API | `DeviceWebTests.Approve_IsRefusedForOperatorAndReadOnly_AtTheService` — `DeviceAdminService.ApproveAsync` / `RejectAsync` / `SaveDiscoverySettingsAsync` check `CurrentUser.Role == Administrator` **in the service**, not only in the page's `[Authorize]`; an Operator or Read-Only call returns a failure result and the device **stays pending**. `SaveAsync` / group edits allow Administrator or Operator. Every mutation writes the audit log (`AuditActions.Device*` / `Stream*`). | PASS |
| **Routing golden oracle** — production router vs independent matcher | `StreamRoutingOracleTests` — 10,000 messages × 50 stream definitions, **0 divergences** (`../oracle-divergence.md`). | PASS |
| **Parameterised SQL** — new stores and migration | `SqliteDeviceStore` / `SqliteDeviceGroupStore` / `SqliteStreamStore` / `SqliteDiscoverySettingsStore` — every user value is a bound `SqliteParameter`; the `IN (…)` scope filter builds `$s0,$s1,…` placeholders, never interpolates ids. Migration `004` is static SQL. | PASS |
| **`event_streams` referential integrity** — routing writes real ids | The router builds `StreamDefinition`s from `SqliteStreamStore` rows only; `event_streams.stream_id` has an FK to `streams`. A benchmark that passed synthetic ids hit the FK immediately — proof the constraint is live. | PASS |
| DAST (OWASP ZAP) | NOT RUN — no browser/Docker on this host (P4-1). Compensating pipeline assertions (`DeviceWebTests`, `StreamScopeAndXssTests`) run auth / role / redirect / encoding against real Kestrel over HTTPS. Carried to Phase 12 / CI. | carried |

No Critical, High, or Medium findings. No `TODO(phase-N)` markers in shipping code.
