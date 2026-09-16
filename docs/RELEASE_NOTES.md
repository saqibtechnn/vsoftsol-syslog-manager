# VSoftSol Syslog Manager — Release Notes

## Unreleased

- **The application can now check for its own updates.** Settings → Updates (off by
  default) periodically checks the vendor's GitHub releases for a newer, cryptographically
  signed version, downloads it, and verifies it — signature, then hash — entirely on its
  own. Nothing installs itself: once verified, the page offers a "Download the installer"
  button, and you run it exactly as described under Upgrading. A release that fails
  verification is never offered and is recorded in the audit log. See the [Admin Guide's
  Automatic update checks section](ADMIN_GUIDE.md#automatic-update-checks).
- **The installer's own .NET Runtime check is fixed — it could never pass, on any machine,
  since v1.0.0.** Every fresh install hit "requires the ASP.NET Core Runtime 8.0.x (Hosting
  Bundle)" regardless of whether that runtime was actually installed: the check read a
  registry path a 64-bit Hosting Bundle install never writes to. If you hit this dialog on
  `v1.0.0` or `v1.1.0` after already installing the Hosting Bundle, that was this bug, not a
  problem with your machine — download the latest installer and it will now pass correctly.

## v1.1.0 — 2026-09-15

- **TOTP MFA is now enforced at sign-in**, closing the one limitation named in v1.0.0's own
  notes below: enabling MFA on an account now actually changes what signing in requires — a
  correct password alone no longer signs you in if MFA is on; a current authenticator code
  or an unused recovery code is required too, with its own independent rate limit.
- **UDP/TCP syslog listener port changes are now live** — closing another limitation named
  in v1.0.0's own notes below. Settings → Listeners gained a "Core syslog (UDP/TCP)" card:
  change either port and it applies immediately, no service restart, with a confirmation
  ("this briefly interrupts collection on that listener") before it takes effect. The new
  port is bound before the old one closes, so a mistyped or already-used port is rejected
  with the previous port left running, never with nothing listening at all. TLS/SNMP/
  Windows Event Log ports remain a restart-time setting, unchanged.
- **The data directory's manual relocation procedure is now actually written down** — the
  Admin Guide's own known limitations below call it "a manual, documented procedure," but no
  such document existed. It does now: see the [Admin Guide's Data directory
  section](ADMIN_GUIDE.md#data-directory). Relocating it remains manual by design — the
  service's least-privilege account cannot grant itself access to an arbitrary new path or
  restart its own service — this is a documentation fix, not a new UI feature.
- **Every stored message now links back to the listener that actually received it.**
  Previously invisible internally; Settings → Listeners gained a read-only "Listener
  identities" card showing the persisted identity behind each protocol (a new one appears
  whenever a port changes, so history is never silently rewritten).
- **Extractors saved in the pattern tester are now actually applied at ingest.** Previously
  a pattern could be built, verified against a sample, and saved — but nothing used it.
  Every enabled saved extractor now runs against every ingested message, including devices
  with no built-in vendor pack at all, which is exactly the gap this feature exists to fill.
  Takes effect on the next collector restart after saving, editing, or disabling one — the
  same as dropping in a new vendor pack.
- **A report whose query fails no longer looks like an empty report.** A malformed custom
  report query, or a compliance-template aggregation this viewer's scope excludes entirely,
  previously rendered an identical "no data" page/CSV to a genuinely empty time range — the
  PDF, the CSV, and the audit log all now say plainly that the report's query didn't run,
  and why.
- **The alert editor's "would have fired" preview is now exact for every alert type.**
  A filtered, distinct-count, or absence alert used to sample the look-back and show an
  "approximately N times" estimate that could change between two clicks of Preview on the
  same, unchanged alert. It now replays every bucket across the whole look-back and shows
  the real count; the only time it says "at least N" now is when the historical scan hits
  its own row cap, in which case it says so plainly instead of hedging with "approximately."
- **A filtered alert on a very busy window now correctly warns when its count is a lower
  bound.** A scan that hit its internal row cap (500,000 events by default) never actually
  triggered the "this count may be incomplete" notification, and could auto-resolve an
  instance that might still have been breaching. Both now work as intended.

## v1.0.0

First general release. On-premises Windows syslog collection and log management: single
node, one installer, no external runtime dependencies (SQLite only).

### Highlights

- **Universal collection, never a rejection.** Any device speaking RFC 3164 or RFC 5424
  syslog is collected with zero configuration on this product's side; a message that fails
  to parse is still stored, in full, and searchable — nothing is ever dropped for coming
  from an unrecognised vendor.
- **17 vendor parsing packs** out of the box: Cisco IOS/IOS-XE, Cisco ASA, Fortinet
  FortiGate, Palo Alto PAN-OS, Juniper JunOS, MikroTik RouterOS, Ubiquiti UniFi, Linux,
  Check Point Gaia, Sophos XG/XGS, SonicWall SonicOS, pfSense, OPNsense, Aruba (AOS-Switch
  and AOS-CX), Huawei VRP, and VMware ESXi — plus a Pattern Tester to add your own with no
  code change or new release.
- **Beyond plain syslog**: a TLS syslog listener, SNMP trap intake (v1/v2c), and an
  authenticated Windows Event Log forwarder endpoint.
- **A real query language** for search — field filters, quoted phrases, boolean operators,
  ranges, wildcards — compiled to parameterised SQL, provably equivalent to a reference
  evaluator across 500 generated queries with zero divergences.
- **Rules and alerts**: per-message rules with retried, dead-lettered actions (email,
  webhook, route to another stream); alerts on aggregations and device-silence over a time
  window, with acknowledge/resolve state so a spike doesn't spam.
- **Dashboards** built from a single generic widget framework — any data source, any
  aggregation, any visualisation — with four shipped defaults including Network Overview.
- **Reports**, including canned compliance templates (a 90-day PCI-DSS report among them),
  on demand or scheduled, delivered by email.
- **Tiered retention** (hot/warm/cold) with Zstd-compressed, SHA-256-verified archives, and
  a disk-usage projection shown before you save a policy, never after.
- **Role-based access** (Administrator/Operator/Auditor/ReadOnly) with per-user stream and
  device-group visibility scope, Argon2id password hashing, self-service TOTP MFA
  enrollment, a complete audit log, and ECDSA-signed, trust-on-first-use configuration
  bundle import/export.
- **A ten-minute out-of-box experience**: a signed MSI installer, an unskippable five-step
  first-run wizard, and a "Waiting for messages" page with copy-ready device commands and
  this server's own address already filled in — the dashboard opens itself the instant the
  first message arrives.

### Installation

See the [Admin Guide](ADMIN_GUIDE.md). Requires the ASP.NET Core Runtime 8.0.x (Hosting
Bundle) and Windows Server 2019 or later. Installs one Windows Service, running as a
dedicated least-privilege virtual account.

### Known limitations in v1.0.0

Recorded here rather than silently omitted, per this project's own testing discipline —
see `PROGRESS.md` for the full, itemised backlog with justification for shipping without
each one:

- **MFA enrollment, not yet enforcement.** TOTP multi-factor authentication can be enrolled
  self-service, but is not yet enforced at sign-in. Tracked for a near-term update.
- **Listener port changes need a manual service restart.** The first-run wizard's ports and
  later hand-edits to the bootstrap override file take effect on the next service restart,
  not live — a deliberate v1 scope boundary to avoid granting the web-facing service
  account rights to control the Windows Service itself.
- **Data directory location is fixed at install time.** The first-run wizard shows and
  confirms the data directory the installer already provisioned with correct permissions;
  relocating it to a different path after install is a manual, documented procedure (stop
  the service, move the directory, update the registry pointer), not yet a wizard or
  Settings option.
- **ASP.NET Core Runtime version check is best-effort.** The installer confirms *some* .NET
  runtime is present, not specifically the required ASP.NET Core 8.0.x version — a missing
  or wrong version fails fast and clearly at service start instead, rather than being
  caught at install time.
- **No API-only cloud sources, flow-record protocols (NetFlow/sFlow/IPFIX), or a dedicated
  endpoint-forwarder agent.** See the User Guide's Device Compatibility chapter for the
  full, explicit statement of what this product collects and what it does not.

### Environmental verification carried forward

A small number of this release's acceptance criteria require infrastructure this
development environment does not have access to — a matrix of clean Windows Server
2019/2022/2025 VMs, real third-party network hardware generating live traffic, and an
in-person unaided usability session with a network administrator who has never seen the
product. These are documented, itemised, and carried as explicit follow-up work rather than
asserted as complete without evidence — see `docs/evidence/phase-12/known-issues.md`. Every
criterion achievable within this environment (installer structure and MSI validation,
service/firewall/ACL configuration, the full test suite against the packaged build, the
first-run wizard and waiting-for-messages flow end-to-end, backup/restore, white-label
rebranding) was executed and its evidence committed.
