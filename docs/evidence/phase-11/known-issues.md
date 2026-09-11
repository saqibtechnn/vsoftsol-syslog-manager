# Phase 11 — known issues

Per TESTING_STANDARDS.md §6, every bug found during this phase, fixed or carried, with its
root cause in one sentence.

## Fixed during this phase (kept as permanent regression tests)

- **`Rfc3164Parser` wrongly tag-stripped a CEF-formatted message's `"CEF:0|Vendor|..."`
  prefix.** Root cause: a colon-terminated BSD tag was accepted as long as a colon
  appeared anywhere in the first token, with no requirement that a real space follow it —
  so `"CEF:0|Check"` (no space after the colon) was treated exactly like `"sudo: message"`
  (a space after the colon). Found by the pre-existing Phase 3 `OracleDifferentialTests`
  the moment the new checkpoint-gaia vendor pack's CEF fixtures exercised the case; fixed
  by requiring the colon to be the last character of its whitespace-delimited token.
  Verified safe against the full 240-fixture oracle run and all 1041 unit tests.
  Permanent memory note: `rfc3164-tag-requires-colon-space`.
- **A new vendor pack's `[match]` rule silently stole another vendor's fixtures.** Root
  cause: `PatternPack.Matches` ORs every `[match]` line, and the aruba-aos-switch pack's
  `message ~ ^[A-Za-z][A-Za-z0-9_-]*:\s` rule was broad enough to also match Juniper's and
  Ubiquiti's own message shapes, at a lower `priority` number than either. Found
  immediately by `VendorFixtureTests` (wrong `vendor` field on an unrelated corpus file).
  Fixed by removing the rule and relying solely on the (already sufficient, already
  correctly scoped) `appname ~ ^(AMM|ProCurve|Aruba)$` rule. Permanent memory note:
  `pattern-pack-match-rules-are-ored-globally`.
- **`SqliteArchiveStore`-adjacent DI wiring gap**: registering the three new
  delegate-dependent listeners (`TlsSyslogListener`/`SnmpTrapListener`/`WinEventLogListener`)
  directly inside `AddSyslogIngestion` broke the pre-existing `IngestionLifecycleTests`
  harness, which builds only `AddSyslogIngestion()` without the Service composition root's
  `TlsCertificateProvider`/`SnmpCommunityProvider`/`WinEventLogApiKeyValidator` delegates.
  Fixed by moving those three listener registrations into `AddCollectorRuntime`, alongside
  the delegates they depend on — `AddSyslogIngestion` stays self-contained (Udp/Tcp only),
  consistent with its existing contract.
- **A flaky `ConfigBundleTests` failure on the first full-suite run**:
  `ApplyAsync_ATamperedDocument_FailsSignatureVerification` failed with
  `DirectoryNotFoundException` inside `ConfigBundleExporter.ExportExtractors`. Root cause:
  four `ConfigBundleTests` cases passed the *shared* `Path.GetTempPath()` directly as the
  patterns directory instead of a uniquely-named subdirectory; under xUnit's parallel
  execution, another test's own temp-database directory (created and deleted concurrently,
  completely unrelated to patterns) raced `Directory.EnumerateDirectories` and the
  enumerator threw when a sibling entry vanished mid-iteration — a test-isolation bug
  (TESTING_STANDARDS.md §2.4: "no shared mutable state between tests"), not a product
  defect. Fixed the tests (each now gets its own unique directory) **and** hardened
  `ExportExtractors` itself to tolerate a directory or file vanishing mid-enumeration
  (`IOException` around each enumeration/read step, skipping just that entry) — a real
  install's patterns directory is not locked against concurrent change either, so the
  defensive fix is warranted regardless of the test bug. Verified with a second full
  clean run.
- **Three pre-existing tests hardcoded "seven default streams."** The new reserved
  `collector.health` self-monitoring stream (seeded is_system, sort_order 999) correctly
  brings the count to eight. Updated deliberately, with the reason recorded in each test
  (`SeedDataTests`, `StreamRoutingIntegrationTests`) — not a weakened assertion, a corrected one.

## Carried (not defects; documented scope decisions — see ADR 0019 and SECURITY_REVIEW.md)

- **B11-1** — config bundle import applies only `is_system` dashboards/reports; a personal
  dashboard/report's ownership cannot be remapped across installs without an identity
  bridge this format does not have. Severity: Low.
- **B11-2** — "listener down" self-monitoring is registry-based; it detects a listener that
  never started or was gracefully stopped, not an in-process crash that bypasses
  `StopAsync`. No such crash path is observed in the current codebase. Severity: Low.
- **B11-3** — TOTP MFA enrollment/verification/recovery-code primitives are complete and
  tested; the login flow does not yet enforce the second factor for
  `users.mfa_enabled = 1` accounts. `MfaSelfServiceService.VerifyLoginCodeAsync` is ready
  for that wiring. Severity: Medium — carried to a Phase 11 follow-up, written
  justification in `docs/security/SECURITY_REVIEW.md`, operator sign-off pending.
- **P10-1** (carried again from Phase 10, unchanged) — the literal 10M-event-backlog
  acceptance run → Phase 12 clean VM.
- **P4-1 / P4-2** (carried, unchanged) — OWASP ZAP DAST / axe-core not run on this build
  host; compensating xUnit assertions in place.
- **Windows Event Log endpoint's `HttpListener` URL ACL** — binding a non-loopback address
  under a non-administrator service account needs a one-time `netsh http add urlacl`
  reservation. Loopback (the shipped default) needs none — verified live
  (`WinEventLogListenerTests` bind and pass without elevation on this host). Carried to the
  Phase 12 installer as a conditional step, only relevant if an operator widens the bind
  address.

## Deferred (stated interpretation, not a defect)

- **Listener enable/bind-address/port settings** (TLS/SNMP/WinEventLog) are shown
  read-only on the Settings → Listeners page, sourced from the service configuration file —
  the same disposition the product has used for the UDP/TCP listener ports since Phase 2.
  The SNMP community string and API keys, which genuinely need runtime changes without a
  restart, are fully editable from that page. A future phase could move the technical
  bind/port/enable settings to the database if operator demand for live editing without a
  restart emerges; today's disposition is consistent with 10 prior phases' precedent, not a
  new gap this phase introduced.
