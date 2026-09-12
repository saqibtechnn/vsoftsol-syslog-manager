# v1.1 — Live UDP/TCP listener port changes — verification

Real, observed output from this environment, per TESTING_STANDARDS.md / this project's
Definition of Done. Not a numbered phase (`START_HERE.md` has no Phase 13), held to the
same evidence bar.

## Build — warning-clean

```
$ dotnet build -c Release
...
Build succeeded.
    0 Warning(s)
    0 Error(s)
```

## Style gate

```
$ dotnet format --verify-no-changes
(no output — exit 0, clean)
```

## New tests — targeted runs

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ListenerRebindTests"
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 2 s

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~BootstrapConfigOverridesTests"
Passed!  - Failed: 0, Passed: 5, Skipped: 0, Total: 5, Duration: 59 ms

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ListenerPortReloadServiceTests"
Passed!  - Failed: 0, Passed: 4, Skipped: 0, Total: 4, Duration: 705 ms

$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release --filter "FullyQualifiedName~ListenerSettingsServiceTests"
Passed!  - Failed: 0, Passed: 3, Skipped: 0, Total: 3, Duration: 619 ms
```

16 new tests, all green.

## Unit tests — full suite, no regressions

```
$ dotnet test tests/VSoftSol.Syslog.UnitTests -c Release
Passed!  - Failed: 0, Passed: 1060, Skipped: 0, Total: 1060, Duration: 59 s
```

Unchanged from before this work — no new unit tests were needed, the new logic is
integration-shaped (real sockets, a real SQLite-backed audit log, a real file on disk).

## Integration tests — full suite

```
$ dotnet test tests/VSoftSol.Syslog.IntegrationTests -c Release
Passed!  - Failed: 0, Passed: 786, Skipped: 0, Total: 786, Duration: 7 m 53 s
```

786 = the 773 baseline from the B11-3 verification + 13 net new tests here (4 + 5 + 4 + 3 =
16, minus 3 that already existed as `BootstrapConfigOverridesTests` cases before this work).
Zero failures — not even the usually load-sensitive, already-documented `P2-5`
(`WalCrashConsistencyTests`) flake reproduced on this run. No regressions.

## Security

No new external dependency. No new attack surface: the operation is Administrator-only
(`AuthPolicies.Administer`, the existing gate on `/settings/listeners`), audited on every
attempt (`AuditActions.ConfigChange`), and — per the ASVS-checklist.md v1.1 section — no
privilege change, since ADR 0005/0020 already made the process this runs in the single
process that owns the listener sockets. See `docs/security/ASVS-checklist.md` and
`docs/security/SECURITY_REVIEW.md` v1.1 sections for the full write-up.

## UI verification

The interactive Blazor circuit (the Apply button → confirmation modal → live rebind →
toast) could not be driven by a real mouse click in this sandbox: both available browser
surfaces (the in-app Browser pane and a real-Chrome connector) refused to proceed past the
app's self-signed HTTPS certificate's interstitial — neither exposes a way to click through
it, and the standard `thisisunsafe` bypass keystroke did not reach the native Chrome error
page (a documented extension-automation limitation, not something this change controls or
introduces — every page in this app hits the same wall). Per this project's own convention
for environment-blocked verification (`docs/evidence/phase-12/known-issues.md`), that
limitation is recorded here rather than a click-through being falsely claimed.

What *was* verified for real: the first-run wizard was driven end-to-end over real HTTP
(scripted POSTs with a cookie jar and the real antiforgery tokens the server issued — the
wizard's own static-SSR forms make this possible), landing on a real authenticated session
against a live collector-hosting instance (`Collector:HostCollectorRuntime=true`, loopback
UDP/TCP ports `15514`/`15515`). Fetching `/settings/listeners` with that session then
confirmed the rendered page:

```html
<section class="ds-card"><h2>Core syslog (UDP/TCP)</h2>
    <p class="ds-muted">
        UDP <span class="ds-chip ds-chip--ok">Enabled</span>
        127.0.0.1:15514
        &nbsp;·&nbsp;
        TCP <span class="ds-chip ds-chip--ok">Enabled</span>
        127.0.0.1:15515</p>
    <form ...><input id="ds-udp-port" ... value="15514" />
               <input id="ds-tcp-port" ... value="15515" />
        <button type="submit" ... disabled>Apply</button>
```

— the correct live port values, correct enabled chips, and the Apply button correctly
disabled because no change was pending (matches `_udpPort == Listeners.Ingestion.UdpPort &&
_tcpPort == Listeners.Ingestion.TcpPort` in the code-behind). The click-driven path behind
that same button calls `ListenerSettingsService.SetUdpTcpPortsAsync`, which is exercised
end-to-end — including a real listener actually rebinding to a new port and the old port
being freed — by `ListenerSettingsServiceTests` and `ListenerRebindTests` above.

## Documentation updated

- `docs/security/ASVS-checklist.md` — new v1.1 section (V1.2/V1.14/V7.1).
- `docs/security/SECURITY_REVIEW.md` — new v1.1 section.
- `docs/RELEASE_NOTES.md` — new "Unreleased" bullet (v1.0.0's own text left untouched).
- `PROGRESS.md` — new v1.1 log entry (see commit).

## Conclusion

The v1.0.0 "listener port changes need a manual service restart" limitation is closed for
the two core syslog ports (UDP/TCP): an Administrator can now change either port from
Settings → Listeners and have it take effect immediately, with the previous listener
guaranteed to keep working if the new port cannot be bound. TLS/SNMP/WinEventLog ports are
unchanged (still restart-tier), a deliberately confirmed scope boundary, not a partial fix.
Zero regressions: unit 1060/1060, integration 786/786.
