# v1.1 — Live UDP/TCP listener port changes — red/green evidence

Per TESTING_STANDARDS.md §2.1. Four test files, each written before the code it exercises.

## 1. `UdpSyslogListener`/`TcpSyslogListener.RebindAsync` — `ListenerRebindTests`

### RED

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ListenerRebindTests"
...
error CS1061: 'UdpSyslogListener' does not contain a definition for 'RebindAsync' ...
error CS1061: 'TcpSyslogListener' does not contain a definition for 'RebindAsync' ...
Build FAILED.
```

A compile-failure RED (the method did not exist yet) — the correct reason: the test names
the API the implementation is about to add.

### Implementation

- `UdpSyslogListener.RebindAsync(int newPort, ct)` / `TcpSyslogListener.RebindAsync(int newPort, ct)`
  — bind a brand-new socket on the new port first; only once that succeeds, cancel and close
  the old socket/loop and swap the listener's `BoundPort`/`Name` to the new values. A bind
  failure (thrown before any old-socket teardown) leaves the original listener completely
  untouched — this protocol is never left with zero listeners (Constraint 3). A private
  `SemaphoreSlim` per listener serializes concurrent rebind attempts. The new receive/accept
  loop's own lifetime token is a fresh, independent `CancellationTokenSource` — deliberately
  **not** linked to the caller's `cancellationToken` (a UI-request-scoped token), since
  linking it would let the listener die the moment the HTTP/Blazor request that triggered
  the rebind completes.

### GREEN

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ListenerRebindTests"
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 2 s
```

Covers: UDP move-to-new-port with the old port verified free afterward and zero messages
lost across the swap; UDP rebind-to-a-busy-port throws and the original listener keeps
accepting; the same two cases for TCP (plus: old connections are closed, new connections on
the new port succeed).

## 2. `BootstrapConfigOverrides.UpdateIngestionPortsAsync` — `BootstrapConfigOverridesTests`

### RED

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~BootstrapConfigOverridesTests"
error CS0117: 'BootstrapConfigOverrides' does not contain a definition for 'UpdateIngestionPortsAsync'
```

### Implementation

A read-merge-write over the existing override file (if any), touching only
`Ingestion.UdpPort`/`TcpPort` — unlike the wizard's own `WriteAsync` (a one-shot write of
every bootstrap setting), this must never clobber the `Kestrel.Endpoints.Https.Url` the
wizard already wrote, since the caller (`ListenerPortReloadService`) only knows about the
two ingestion ports.

### GREEN

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~BootstrapConfigOverridesTests"
Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 59 ms
```

New cases: a targeted port update preserves the wizard's existing HTTPS port; the same call
with no pre-existing file creates one holding just the two ports.

## 3. `ListenerPortReloadService` — `ListenerPortReloadServiceTests`

### RED

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ListenerPortReloadServiceTests"
error CS0246: The type or namespace name 'ListenerPortReloadService' could not be found
```

### Implementation

Orchestrates the two pieces above against real `ISyslogListener` instances: attempts the
live rebind first; only on success, relinks the matching `ActionExecutorOptions.
LocalSyslogEndpoints` entry (mutated in place — that list is a shared reference the rule
engine's loop guard already reads everywhere, so no cache invalidation is needed), updates
the shared `IngestionOptions` instance so any other reader (the Settings page) sees the live
port immediately, and persists to the override file. `CanApplyLive` is false whenever no
UDP/TCP `ISyslogListener` is registered in this process (the documented dev-only
standalone-Web case) — the one signal the Web layer needs to fall back to the
restart-required message.

### GREEN

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ListenerPortReloadServiceTests"
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 705 ms
```

## 4. `ListenerSettingsService.SetUdpTcpPortsAsync` — `ListenerSettingsServiceTests`

### RED

```
$ dotnet build -c Release
error CS1729: 'ListenerSettingsService' does not contain a constructor that takes 8 arguments
error CS1061: 'ListenerSettingsService' does not contain a definition for 'SetUdpTcpPortsAsync'
```

### Implementation

Validates the requested port(s) (1-65535, reject a no-op call with both ports null) before
ever touching `ListenerPortReloadService`; audits every attempt that reaches the reload
service — accepted or refused — under `AuditActions.ConfigChange`, the same convention
`SetSnmpCommunityAsync` already established on this page.

### GREEN

```
$ dotnet build -c Release
Build succeeded. 0 Warning(s), 0 Error(s)

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ListenerSettingsServiceTests"
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 619 ms
```
