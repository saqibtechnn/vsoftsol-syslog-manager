# PHASE 11 — Extras & Security Hardening

## Context
The product is functionally complete for the common case. This phase adds the remaining
intake protocols, the operability features, and the security work that must be done
before anyone installs this on a production network.

## Objective
TLS and SNMP intake, Windows Event Log support, config portability, self-monitoring,
and a completed security review.

## Build
1. **TLS syslog listener** (default 6514) — server certificate from the Windows
   certificate store or a PFX path, configurable minimum TLS version (default 1.2),
   optional mutual TLS with client certificate validation.
2. **SNMP trap receiver** (default 162) — v1 and v2c, community string configuration,
   MIB-free varbind capture, traps normalized into the canonical event schema with
   `protocol = snmp`.
3. **Windows Event Log intake endpoint** — accept forwarded Windows events and normalize
   them into the same schema with `protocol = wineventlog`, mapping level to severity
   and channel to facility. Windows has no native syslog; this closes that gap.
3a. **Extended parser packs** — the seven listed for Phase 11 in `VENDOR_SUPPORT.md`:
   Check Point Gaia (including CEF/LEEF output from `cp_log_export`), Sophos XG/XGS,
   SonicWall SonicOS, pfSense/OPNsense, Aruba AOS-Switch and AOS-CX, Huawei VRP, and
   VMware ESXi. Same rule as Phase 3: runtime-loaded files, 10 fixtures each, no
   recompilation to add a vendor.
4. **Configuration bundles** — export rules, streams, devices, groups, users (without
   password hashes), dashboards, reports, and extractors as a signed JSON bundle. Parser
   packs export as bundles too — this is how a vendor pack is shipped to a customer
   between releases.
   Import with a preview-and-merge step. This is the "content pack" pattern and it is
   how you ship pre-built vendor packs to customers.
5. **Self-monitoring page** — queue depth, spill queue size, ingest rate, drop counter,
   database size, disk free, WAL checkpoint status, last archive job, per-listener status.
6. **Self-alerts** — disk below threshold, non-zero drop counter, sustained queue depth,
   listener down, archive verification failure.
7. The application emits its own health events into a reserved internal stream, so the
   collector is monitored the same way everything else is.
8. **TOTP MFA** for Administrator accounts, with recovery codes.
9. **Security review pass** — complete and commit `docs/SECURITY_REVIEW.md` covering:
   input validation on every listener, SQL parameterization audit, XSS review of every
   place a raw message is rendered, path traversal on file actions and archive paths,
   script action allow-list, secret handling, TLS configuration, session management,
   rate limiting on login, and dependency vulnerability scan output.

## Do not build in this phase
The installer or user documentation — Phase 12.

## Tests to write first
- TLS handshake test including a rejected client under mutual TLS.
- SNMP trap test: send v1 and v2c traps, assert normalization.
- Windows Event Log test: forward a sample event, assert severity and facility mapping.
- Config bundle round trip: export from instance A, import to a clean instance B, assert
  functional equivalence.
- **XSS test**: ingest a message containing `<script>` and HTML entities, assert it
  renders escaped in the grid, the context view, the export, and the PDF report.
- Path traversal test on file-write actions and archive paths.
- MFA test including recovery code use and reuse rejection.

## Verification — run these and paste output
```bash
dotnet test
dotnet list package --vulnerable --include-transitive
```
The vulnerability scan must be clean, or every finding must have a written justification
in `docs/SECURITY_REVIEW.md`.

## Validation & Evidence (per `TESTING_STANDARDS.md`)

- **Protocol conformance** — TLS 1.2 and 1.3 handshakes, rejected weak ciphers, expired
  cert, wrong hostname, revoked cert, and mutual-TLS rejection of an unknown client.
  SNMP v1 and v2c traps with malformed varbinds, oversized OIDs, and wrong community.
- **Structured penetration checklist**, executed and documented in
  `docs/SECURITY_REVIEW.md`: injection (SQL, command, LDAP, log), XSS (stored and
  reflected — ingest `<script>` and confirm escaping in grid, context view, live tail,
  CSV, JSON, **and PDF**), path traversal on every file and archive path, SSRF on the
  webhook action, XXE on config bundle import, deserialization of untrusted bundles,
  privilege escalation between roles, and rate-limit bypass on login.
- **Dependency and supply chain** — `dotnet list package --vulnerable --include-transitive`
  clean, plus a committed SBOM. Every accepted finding needs a written justification.
- **Config bundle round trip** — export from A, import to clean B, assert functional
  equivalence by running **the entire prior-phase test suite** against B.
- **Full regression** — every test from Phases 0-10 green, plus the Phase 2 chaos suite
  and the 24h soak re-run against the final feature set.
- **Evidence:** completed `SECURITY_REVIEW.md`, TLS/SNMP conformance results, XSS matrix
  across all six output surfaces, SBOM, vulnerability scan, full regression run.

## Security Validation (per `SECURITY_STANDARDS.md`)

This phase's existing scope **is** the security phase; the additions below make it verifiable.

- **XXE and deserialization on config bundle import** — external entity expansion,
  billion-laughs, zip-slip, oversized bundles, malformed signatures, and a bundle signed
  by an untrusted key. Import must be schema-validated and signature-verified **before**
  any content is processed.
- **TLS configuration scan** — testssl.sh or sslyze against both the UI and the TLS
  syslog listener. Grade A, no TLS 1.0/1.1, no weak ciphers, no renegotiation flaws.
  Verify mutual TLS rejects unknown and revoked client certificates.
- **SNMP intake security** — community strings are secrets (DPAPI, never logged),
  malformed varbinds and oversized OIDs are rejected without crashing, and the default
  configuration is not `public`.
- **Windows Event Log endpoint** — authenticated, rate-limited, and unable to be used to
  forge events attributed to another host.
- **Full DAST** — ZAP full scan across every page added since Phase 4.
- **Threat model review #3** — final pass; every residual risk explicitly accepted with a
  reason.
- **ASVS L2 completion** — every control resolved. Gaps go in `SECURITY_REVIEW.md` with
  justification.
- **Evidence:** bundle-attack matrix, TLS scan grades, SNMP fuzz results, ZAP report,
  completed ASVS checklist, updated threat model.

## Definition of Done
Standard DoD, plus `docs/SECURITY_REVIEW.md` is complete and every item is either fixed
or explicitly accepted with a reason.

## Commit
`feat: phase 11 — tls, snmp, wineventlog, config bundles, self-monitoring, hardening` → tag `v1.0.0-phase.11`
