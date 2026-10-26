# v1.1 close-out — what is still open, and why

Nothing below is a Critical or High finding against the shipped product. Each item names what
it needs; none can be finished by more code in this repository.

## Reduced by this close-out

| Marker | Status |
|---|---|
| P2-5 `WalCrashConsistencyTests` load flake | Hardened (120 s start timeout; collections no longer overlap). Keep monitoring on a CI host. |
| P7-5 Argon2 decoy-timing flake | Hardened (fastest-of-9; collections no longer overlap). Keep monitoring. |
| P4-2 / P5-2 axe-core | **Partly closed.** axe-core 4.10.0 (WCAG 2.0/2.1/2.2 A+AA and best-practice) found and this pack fixed two issues on the unauthenticated pages (first-run wizard, and by the same layout, login/change-password/denied/error). The remaining part is below. |

## Still open

| Marker | Needs | Why it cannot be done here |
|---|---|---|
| P4-2 / P5-2 (remainder) | A browser that trusts the app's HTTPS certificate | The in-app browser rejects the self-signed dev certificate, and trusting it would change the machine's certificate trust store. The scan therefore ran on server-rendered HTML of `/setup` served as static files with the app's own CSS. Not scanned: signed-in screens (dashboards, search, alerts, settings, audit), anything Blazor renders after the circuit connects, live keyboard/screen-reader traversal, and the 1366×768 screenshot. Run axe over each route from a trusted-cert browser, or add a Playwright+axe CI job. |
| P4-1 OWASP ZAP DAST | ZAP installed, same trusted-cert browser/host | Not installed. |
| P1-1, P3-2, P5-1, P6-1, P7-4, P8-3, P10-1 | A clean benchmark VM of record | Throughput and latency gates read marginal on a shared dev host; a 50M-event seed and a 10M-event backlog are multi-hour runs. |
| P3-1, P8-2, P3-3 | Docker/daemon host; Stryker VsTest adapter | rsyslog/syslog-ng live oracle, live DeviceSilent, mutation run. |
| P7-3 | A SQLite ODBC driver installed | `WriteToOdbc` live round trip. |
| P4-3 | An upstream AngleSharp fix | Blazor component-test library is banned until then (see memory `razor-component-testing-htmlrenderer`). |
| B11-1, B11-2 | A design decision | Config-bundle import of personal content; listener-crash detection. Deliberately deferred. |
| ImageSharp advisories | **Operator decision** | Build-tool-only and suppressed with a written rationale; remove by moving the branding tool to ImageSharp 3.x (Six Labors Split License) or to `System.Drawing`. See `docs/security/SECURITY_REVIEW.md`. |
| Code-signing certificate, production release-signing key, external penetration test, unaided usability session | The vendor | Unchanged from v1.0.0 / v1.1.1. |

## Environment note

The host that ran this pack reports 8 logical processors; earlier phases were recorded on a 2-vCPU
VM. P2-5 and P7-5 were first recorded on that VM; the wider set of 14 contention failures was seen
on this 8-processor host. A host with more cores did not remove the problem, which is why the fix is
serialisation rather than a larger timeout alone.
