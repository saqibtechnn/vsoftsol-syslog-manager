# VSoftSol Syslog Manager — Release Notes

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
