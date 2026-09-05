# PHASE 12 — Packaging & Release

## Context
Final phase. Everything works on a developer machine. This phase makes it installable by
someone who has never seen the code, and proves it on a clean VM.

## Objective
A signed MSI, a first-run experience, complete documentation, and a passing end-to-end
acceptance run.

## Build
1. **WiX MSI installer**:
   - Installs the Windows Service and registers it for automatic start with delayed start
   - Creates the data directory with correct ACLs (service account only)
   - Adds firewall rules for the configured listener ports, and **removes them on uninstall**
   - Generates a self-signed HTTPS certificate at install with a documented path to
     replacing it with a real one
   - Upgrade path that preserves the database and configuration
   - Clean uninstall: no orphaned services, files, registry keys, firewall rules, or
     certificates. Offer to retain or remove the data directory.
2. **First-run wizard** — maximum 5 steps, unskippable, per `UX_STANDARDS.md` §2:
   admin password → listener ports → data directory → retention preset (Small / Medium /
   Large, each showing its projected disk usage inline) → optional vendor config bundle
   import. Nothing else goes in the wizard.
2a. **"Waiting for messages" landing page** shown immediately after the wizard. It
   displays copy-ready device configuration commands with **this server's own IP and
   port already substituted in**, taken verbatim from the configuration section of
   `VENDOR_SUPPORT.md` — all 20+ platforms, in a searchable picker with the eight most
   common pinned to the top. `<SYSLOG_IP>` and `<PORT>` are replaced at render time. It auto-advances to the Network Overview
   dashboard the moment the first message arrives. This page is the product's first
   impression and the single highest-leverage screen in the build — treat it as such.
2b. **In-app help**: a searchable shortcut list (`?`), field-level one-sentence help
   text throughout, and a Getting Started checklist on the dashboard that tracks
   first device added / first rule created / first alert configured, and dismisses itself
   when complete.
3. **Version stamping**: assembly version, UI footer, and an About page showing version,
   build date, and license state.
3a. **Installer branding** from `branding/` per `BRANDING.md`: `app-icon.ico` on the MSI,
    the service entry, and Add/Remove Programs; `installer-banner.bmp` and
    `installer-dialog.bmp` in the WiX UI; publisher set to `vendorName`; support URL and
    contact from `brand.json`. The wide logo appears on every first-run wizard step.
    **Rebranding test**: replace `branding/logo.png` and `brand.json`, rebuild the MSI,
    and confirm the installer, the service entry, the UI, and a generated PDF report all
    show the new brand with zero source changes. This is the white-label acceptance test.
4. **Documentation** in `docs/`:
   - *Admin Guide* — install, upgrade, backup/restore, certificate replacement,
     port configuration, service account, troubleshooting
   - *User Guide* — search syntax reference, building rules and alerts, dashboards,
     reports, plus a **Device Compatibility** chapter built from `VENDOR_SUPPORT.md`:
     the three support tiers, the per-vendor configuration commands, how to author a new
     parser pack with the pattern tester, and an honest statement of the
     "not supported in v1" list (API-only cloud sources, NetFlow/sFlow/IPFIX, endpoint
     agents). State the limits plainly rather than letting a customer discover them.
   - *Sizing Guide* — messages/sec vs. CPU, RAM, and disk, with the retention maths and
     worked examples for 50, 200, and 1,000 devices
   - *Release Notes* for v1.0.0
5. **Backup and restore procedure** for the database and configuration, tested and documented.

## Do not build in this phase
Any new feature. If something is missing, record it in PROGRESS.md as v1.1 scope.

## Tests to write first
- Installer test on a clean Windows Server VM: install → verify service running → verify
  firewall rules present → uninstall → verify nothing left behind.
- Upgrade test: install v1.0.0-phase.11 build, populate data, upgrade, assert data and
  configuration survive.

## Verification — the full acceptance run, on a clean VM
Execute every v1 acceptance criterion from `BUILD_PLAN.md` and record the result:
1. Clean VM → installer → live syslog from a real (or simulated) Cisco switch in under
   10 minutes with no config-file editing
2. All performance targets met, benchmark output committed
3. Hard kill at 5,000 msg/sec → zero committed messages lost
4. Every setting changeable from the UI; no second console, no config file
5. Auditor role produces a 90-day PCI-DSS report including archived data, unaided
6. Every unparseable message stored, searchable by raw text, in the Parse Failures stream
7. Audit log is a complete ordered record of every config change made during the run
8. Line coverage ≥ 80% on Ingestion, Rules, Reporting
9. An untrained network admin installs, configures a device, and finds a specific
   message — UI only, no documentation, no help
10. Every primary task reachable in ≤ 5 clicks and completable keyboard-only
11. Every screen passes the five-point UX gate in `UX_STANDARDS.md`

```bash
dotnet build -c Release
dotnet test /p:CollectCoverage=true /p:Threshold=80 /p:ThresholdType=line
dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --filter "*"
```

## UX gate — the real one
The eleven acceptance criteria include a live usability test. Find a network or systems
admin who has **never seen this product**, hand them the installer and nothing else, and
observe. Do not help. Do not explain. Record every point where they hesitate, backtrack,
or reach for documentation. Each hesitation is a defect: fix it or write it into the v1.1
backlog with a justification for shipping without it.

Ten minutes from installer to first message received, unaided, is the pass condition.

## Validation & Evidence (per `TESTING_STANDARDS.md`)

Final validation. Nothing here is self-assessed.

- **Installation matrix** on clean VMs — Windows Server 2019, 2022, and 2025; fresh
  install, upgrade from phase.11, repair, and uninstall. After each uninstall, assert no
  orphaned service, files, registry keys, firewall rules, or certificates.
- **Upgrade data safety** — install, ingest 1M events, configure rules and dashboards,
  upgrade, assert every event, rule, dashboard, and user survives byte-identical.
- **Full regression** — the entire suite from Phase 0 onward, plus the 24h soak and the
  chaos suite, against the packaged build rather than a dev build.
- **Real-device acceptance** — a physical or virtual Cisco switch, a FortiGate, and a
  Linux host sending live traffic simultaneously for 1 hour. Assert correct parsing,
  routing, alerting, and zero loss.
- **Live usability test** — an untrained network admin, the installer, no help, observed.
  Every hesitation recorded as a defect.
- **Sizing validation** — run at 50, 200, and 1,000 simulated devices; record actual CPU,
  RAM, and disk consumption and confirm the Sizing Guide numbers are measured, not estimated.
- **Release sign-off** — all eleven acceptance criteria with committed evidence, the
  §9 sign-off block with zero FAILs, and a written v1.1 backlog for anything deferred.
- **Evidence:** `docs/evidence/phase-12/` containing the install matrix, upgrade
  verification, regression run, real-device capture, usability observation notes, and
  sizing measurements.

## Security Validation (per `SECURITY_STANDARDS.md`)

- **Full penetration test** per `SECURITY_STANDARDS.md` §7, executed against the
  **packaged build on a clean VM**, not a dev build. Output:
  `docs/security/PENTEST_REPORT.md` with finding, CVSS v3.1 score, reproduction steps,
  and resolution.
- **Installer security review** — data directory ACLs grant only the service account;
  the service runs as a dedicated low-privilege account, not LocalSystem; firewall rules
  are scoped to the configured ports and removed on uninstall; the generated certificate
  has a sane key size and lifetime; no world-writable paths; no credentials in the MSI
  or in installer logs.
- **No-backdoor assertion** — an automated test proves the release build contains no
  default credentials, no debug or diagnostic endpoint, no test-only authentication
  bypass, and no hardcoded key. Run it against the shipping binary.
- **Final security regression** — every security test from Phases 0-11 against the
  packaged build.
- **Hardening guide** in `docs/` — network placement, service account setup, certificate
  replacement, TLS-only configuration, firewall rules, backup encryption, and what the
  product does **not** protect against (UDP source spoofing, an attacker with file-system
  access to archives). State the limits plainly.
- **Release gate: zero open Critical or High findings.** Every Medium and Low is
  documented with an operator-signed justification. This gate is not waivable.
- **Evidence:** `docs/evidence/phase-12/security/` with the pentest report, installer
  ACL audit, no-backdoor test output, and the final regression run.

## Definition of Done
All eleven acceptance criteria pass with recorded evidence in PROGRESS.md. Anything not
met is either fixed or moved to a written v1.1 backlog — never silently dropped.

## Commit
`chore: release v1.0.0` → tag `v1.0.0`
