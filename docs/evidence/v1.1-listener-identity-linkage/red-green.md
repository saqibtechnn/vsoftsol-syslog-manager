# v1.1 — listener identity linkage (P2-1 closed) — red/green evidence

Per TESTING_STANDARDS.md §2.1. `docs/evidence/phase-02/known-issues.md`'s exact P2-1
wording: "`events.listener_id` is stored as NULL. Linking events to a persisted `listeners`
row needs the listener-management UI." The `listeners` table (migration 001) and the
`events.listener_id` column/FK have existed since Phase 1/2 — nothing had ever written to
either.

## 1. `SqliteListenerStore` — `SqliteListenerStoreTests`

### RED
```
$ dotnet build tests/VSoftSol.Syslog.IntegrationTests -c Release
error CS0234: The type or namespace name 'Listeners' does not exist in the namespace 'VSoftSol.Syslog.Data'
```

### Implementation
`UpsertAsync(protocol, bindAddress, port, enabled, ct)` keys the SQL `ON CONFLICT` on
`name` — a string built the same way every `ISyslogListener` already builds its own `Name`
(`"{protocol}:{bindAddress}:{port}"`). An unchanged restart therefore reuses the same row
and id; a changed port (a live rebind, or a config edit) produces a different `name` and so
a genuinely new row/id — events already committed under the old id keep resolving
correctly, since nothing is rewritten in place.

### GREEN
```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~SqliteListenerStoreTests"
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 292 ms
```

## 2. `ListenerRegistrationHostedService` — `ListenerRegistrationHostedServiceTests`

### RED
```
$ dotnet test ... --filter "FullyQualifiedName~ListenerRegistrationHostedServiceTests"
error CS0246: The type or namespace name 'ListenerRegistrationHostedService' could not be found
```

### Implementation
Upserts one row per protocol from **configuration** (not each listener's actual
`BoundPort`) — a real install never configures an ephemeral port 0, so registration does
not need to wait on `IngestionHostedService` finishing listener startup; it only needs the
database (migrated by `DatabaseInitializer`, already registered first).

### GREEN
```
$ dotnet test ... --filter "FullyQualifiedName~ListenerRegistrationHostedServiceTests"
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 125 ms
```

## 3. Enrichment wiring — `ListenerIdEnrichmentTests`

### RED (a genuine runtime bug, not just a missing type)
The first draft of this test hardcoded a synthetic `listener_id` (`42`) in the registry
without actually creating that row. `events.listener_id REFERENCES listeners(listener_id)`
(migration 001) with `PRAGMA foreign_keys = ON` (`SqliteConnectionFactory.cs`) turned that
into a real FK violation on commit, and `IngestionPipeline`'s zero-message-loss retry logic
retried forever — the test hung and timed out at 60s instead of failing fast. This is the
exact "synthetic id → FK violation → silent infinite retry loop" pitfall already documented
for stream routing; it applies identically here. Fixed by upserting a **real** listener row
via `SqliteListenerStore` against the harness's own database before ingesting anything —
exactly how production populates `ListenerIdRegistry`, since nothing else ever puts an id
into it that `SqliteListenerStore` did not just issue.

```
$ dotnet test ... --filter "FullyQualifiedName~ListenerIdEnrichmentTests"
Failed VSoftSol.Syslog.IntegrationTests.Ingestion.ListenerIdEnrichmentTests.IngestedMessage_IsStampedWithTheRegisteredListenerId [1 m]
  System.TimeoutException : The operation has timed out.
Failed!  - Failed: 1, Passed: 1, Skipped: 0, Total: 2
```

### Implementation
`SyslogEvent.WithListenerId(long)` (mirrors the existing `WithRouting`/`WithRuleOutcome`
copy-builder pattern). `SyslogPlatformExtensions.AddCollectorRuntime`'s `EventEnricher`
resolves `ListenerIdRegistry.TryGetId(parsed.Protocol, ...)` — a pure in-memory lookup, no
per-event database cost — before routing/rules run, so `WithRouting`'s own copy preserves
the resolved id through to the final committed event.

### GREEN
```
$ dotnet test ... --filter "FullyQualifiedName~ListenerIdEnrichmentTests"
Passed!  - Failed: 0, Passed: 2, Skipped: 0, Total: 2, Duration: 170 ms
```

## 4. Live-rebind interaction — extended `ListenerPortReloadServiceTests`

### RED
```
error CS1729: 'ListenerPortReloadService' does not contain a constructor that takes 7 arguments
```
(3 call sites needed updating for the two new constructor dependencies.)

### Implementation
After a successful UDP/TCP rebind, `ListenerPortReloadService` also upserts a new
`listeners` row for the new port and updates `ListenerIdRegistry` — otherwise every event
received after a live port change would keep carrying the stale, pre-rebind `listener_id`,
silently defeating the whole point of this feature.

### GREEN
```
$ dotnet test ... --filter "FullyQualifiedName~ListenerPortReloadServiceTests"
Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: (part of the 8-test run below)
```

## 5. Web UI passthrough — extended `ListenerSettingsServiceTests`

### RED
```
error CS1729: 'ListenerSettingsService' does not contain a constructor that takes 9 arguments
error CS1061: 'ListenerSettingsService' does not contain a definition for 'ListRegisteredListenersAsync'
```

### Implementation
`ListenerSettingsService.ListRegisteredListenersAsync` — a thin passthrough to
`SqliteListenerStore.ListAsync`, for a new read-only "Listener identities" card on
Settings → Listeners (system-managed rows, not admin-editable).

### GREEN
```
$ dotnet test ... --filter "FullyQualifiedName~ListenerSettingsServiceTests"
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 694 ms
```

## Combined

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ListenerPortReloadServiceTests|FullyQualifiedName~ListenerSettingsServiceTests"
Passed!  - Failed: 0, Passed: 8, Skipped: 0, Total: 8, Duration: 1 s
```
