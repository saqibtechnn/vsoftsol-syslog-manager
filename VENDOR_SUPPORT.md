# VENDOR_SUPPORT.md — Device Compatibility & Configuration Reference

Two purposes: it defines what "supported" means for the build, and it is the **source
content for the Phase 12 "Waiting for messages" page**, where these commands appear with
the server's own IP and port already substituted in.

Throughout, `<SYSLOG_IP>` and `<PORT>` are placeholders the UI replaces at render time.

---

## The three support tiers

| Tier | What it means | Coverage |
|---|---|---|
| **1 — Collection** | Message received, stored, searchable, alertable. Zero configuration on our side. | **Every device that speaks RFC 3164 or RFC 5424.** This is not a vendor list; it is the protocol. |
| **2 — Parsing** | Log content broken into named fields (`src_ip`, `username`, `action`, `interface`, `acl_name`) so rules and dashboards can target them precisely. | Ships with patterns for the vendors marked below. **User-extensible without a code change** via the Phase 5 pattern tester. |
| **3 — Non-syslog intake** | Sources that cannot send syslog at all. | SNMP traps (v1/v2c) and Windows Event Log, both Phase 11. |

The Phase 3 fallback chain guarantees Tier 1 universally: try RFC 5424 → fall back to
RFC 3164 → store raw with `parse_status = raw`. **A message is never rejected for coming
from an unrecognised vendor.** This is the single most important compatibility guarantee
in the product and it must not be weakened for any optimisation.

---

## Parser pack roadmap

Patterns live in `src/VSoftSol.Syslog.Ingestion/Patterns/<vendor>/`, are plain text, and
are loaded at runtime — adding one is a file drop, not a release.

**Ship in Phase 3 (core eight) — SHIPPED, `v1.0.0-phase.3`:**
Cisco IOS / IOS-XE · Cisco ASA · Fortinet FortiGate (KV) · Palo Alto PAN-OS (positional
CSV per log type) · Juniper JunOS · MikroTik RouterOS · Ubiquiti UniFi ·
Linux (`sshd`, `sudo`, `cron`, `kernel`). Each pack: 25 committed fixtures with expected
output in `tests/fixtures/messages/<vendor>/corpus.jsonl` (200 total). Packs are plain
text in `src/VSoftSol.Syslog.Ingestion/Patterns/<vendor>/`, loaded at runtime (ADR 0011).

**Ship in Phase 11 (extended seven):**
Check Point Gaia · Sophos XG/XGS · SonicWall SonicOS · pfSense / OPNsense ·
Aruba (AOS-Switch and AOS-CX) · Huawei VRP · VMware ESXi

**Community / post-v1, authored through the pattern tester:**
Cisco NX-OS · Cisco Meraki · Extreme EXOS · Dell OS10 · F5 BIG-IP · WatchGuard ·
Synology DSM · Zyxel · Netgear · TP-Link Omada · Windows via NXLog

Every pack must ship with at least 10 fixture messages and their expected parse output
in `tests/fixtures/messages/<vendor>/`, per the Phase 3 requirement.

---

## Device configuration commands

These are the copy-ready blocks for the "Waiting for messages" page and the User Guide.

### Cisco IOS / IOS-XE
```
configure terminal
 service timestamps log datetime msec localtime show-timezone
 logging host <SYSLOG_IP> transport udp port <PORT>
 logging trap informational
 logging source-interface <INTERFACE>
 logging on
end
write memory
```

### Cisco ASA
```
configure terminal
 logging enable
 logging timestamp
 logging host <INTERFACE_NAME> <SYSLOG_IP>
 logging trap informational
end
write memory
```

### Cisco Nexus (NX-OS)
```
configure terminal
 logging timestamp milliseconds
 logging server <SYSLOG_IP> 6 facility local7
end
copy running-config startup-config
```

### Fortinet FortiGate
```
config log syslogd setting
 set status enable
 set server "<SYSLOG_IP>"
 set port <PORT>
 set mode udp
 set facility local7
 set format default
end
config log syslogd filter
 set severity information
end
```

### Palo Alto PAN-OS
GUI: **Device → Server Profiles → Syslog → Add** — name it, add a server with
`<SYSLOG_IP>`, port `<PORT>`, transport UDP, format BSD.
Then **Objects → Log Forwarding → Add**, attach the syslog profile to the log types you
want, and apply that Log Forwarding profile to your security rules. Commit.
Traffic logs only forward if the profile is attached to the rules — this is the step
people miss.

### Juniper JunOS
```
configure
 set system syslog host <SYSLOG_IP> any info
 set system syslog host <SYSLOG_IP> port <PORT>
 set system syslog host <SYSLOG_IP> source-address <MGMT_IP>
 set system syslog time-format year millisecond
commit and-quit
```

### MikroTik RouterOS
```
/system logging action add name=vsoftsol target=remote \
    remote=<SYSLOG_IP> remote-port=<PORT> src-address=<MGMT_IP>
/system logging add topics=info action=vsoftsol
/system logging add topics=error action=vsoftsol
/system logging add topics=warning action=vsoftsol
```

### Ubiquiti UniFi
Controller GUI: **Settings → System → Advanced → Remote Logging** — enable Syslog, set
server `<SYSLOG_IP>` and port `<PORT>`. Enable "Debug" only if you want verbose device logs.

### Check Point (Gaia)
```
add syslog log-remote-address <SYSLOG_IP> level info
save config
```
For security event forwarding on R80.20+, use `cp_log_export`:
```
cp_log_export add name vsoftsol target-server <SYSLOG_IP> \
    target-port <PORT> protocol udp format syslog
cp_log_export restart name vsoftsol
```

### Sophos XG / XGS
GUI: **Configure → System services → Log settings → Syslog servers → Add** — name,
IP `<SYSLOG_IP>`, port `<PORT>`, facility `DAEMON`, severity `Information`.
Then tick the log categories to forward.

### SonicWall SonicOS
GUI: **Device → Log → Syslog → Syslog Servers → Add** — `<SYSLOG_IP>`, port `<PORT>`,
format "Default" or "Enhanced". Set severity under **Device → Log → Settings**.

### pfSense
GUI: **Status → System Logs → Settings** — enable Remote Logging, set Remote log server
to `<SYSLOG_IP>:<PORT>`, choose the log types to send.

### OPNsense
GUI: **System → Settings → Logging / targets → Add** — transport UDP(4),
hostname `<SYSLOG_IP>`, port `<PORT>`, applications/levels as needed.

### Aruba AOS-Switch (ProCurve)
```
configure
 logging <SYSLOG_IP>
 logging facility local7
 logging severity info
 timesync sntp
write memory
```

### Aruba AOS-CX
```
configure
 logging <SYSLOG_IP> udp <PORT> severity info
write memory
```

### Huawei (VRP)
```
system-view
 info-center enable
 info-center loghost <SYSLOG_IP> facility local7
 info-center source default channel loghost log level informational
quit
save
```

### Extreme Networks EXOS
```
configure syslog add <SYSLOG_IP>:<PORT> local7
enable log target syslog <SYSLOG_IP>:<PORT> local7
save configuration
```

### Dell OS10
```
configure terminal
 logging server <SYSLOG_IP> udp <PORT>
 logging severity informational
end
write memory
```

### F5 BIG-IP
```
tmsh modify /sys syslog remote-servers add { vsoftsol { host <SYSLOG_IP> remote-port <PORT> } }
tmsh save /sys config
```

### VMware ESXi
```
esxcli system syslog config set --loghost='udp://<SYSLOG_IP>:<PORT>'
esxcli system syslog reload
esxcli network firewall ruleset set --ruleset-id=syslog --enabled=true
esxcli network firewall refresh
```

### Linux — rsyslog
Create `/etc/rsyslog.d/60-vsoftsol.conf`:
```
*.* @<SYSLOG_IP>:<PORT>
```
Use `@@` instead of `@` for TCP. Then:
```
sudo systemctl restart rsyslog
```

### Linux — syslog-ng
Add to `/etc/syslog-ng/conf.d/vsoftsol.conf`:
```
destination d_vsoftsol { syslog("<SYSLOG_IP>" transport("udp") port(<PORT>)); };
log { source(s_src); destination(d_vsoftsol); };
```
```
sudo systemctl restart syslog-ng
```

### Cisco Meraki
Dashboard: **Network-wide → General → Reporting → Syslog servers → Add** —
`<SYSLOG_IP>`, port `<PORT>`, select the roles (Event log, Flows, URLs, Air Marshal).
The MX/MS must have a route to the collector; Meraki sends from the local appliance, not
the cloud.

### Synology DSM
**Log Center → Log Sending** — enable, server `<SYSLOG_IP>`, port `<PORT>`,
transfer protocol UDP, log format BSD.

### Windows
Windows has no native syslog client. Two options:
1. Use the built-in Windows Event Log endpoint added in Phase 11.
2. Install NXLog Community Edition and forward to `<SYSLOG_IP>:<PORT>`.

---

## Known compatibility traps — handle these in Phase 3

Each needs a fixture and a passing test. **Status after Phase 3:**

- **Cisco sequence numbers and the leading `%`** — ✅ the `cisco-ios` pack extracts
  `cisco_facility` / `cisco_severity` / `cisco_mnemonic` / `cisco_detail`; the RFC 3164
  parser strips the sequence number and the sub-second timestamp.
- **Cisco ASA message IDs** (`%ASA-6-302013`) — ✅ `asa_message_id` extracted by the
  `cisco-asa` pack.
- **Missing year in RFC 3164 timestamps** — ✅ inferred from `received_utc`, handling the
  31 Dec / 1 Jan rollover both ways (`YearRolloverTests`). An explicit 4-digit year
  (Cisco ASA `logging timestamp`) is honoured.
- **Devices sending local time with no timezone** — ✅ stored as the wall-clock instant and
  flagged `timestamp_ambiguous`. The per-device timezone override is Phase 6.
- **FortiGate key-value format** — ✅ parsed by the KV extractor, not GROK.
- **Palo Alto CSV format** — ✅ one `[csv]` stage per log type (`when = 3=<TYPE>`),
  positional columns per type.
- **Check Point CEF/LEEF output** — deferred to Phase 11 with the extended seven.
- **Multi-line messages** — ✅ each datagram stored as its own event; no reassembly in v1
  (User Guide limitation).
- **Oversized messages** — ✅ accepted up to `Ingestion:MaxMessageBytes` / `Parsing:
  MaxMessageChars`, `truncated` field set, never dropped.
- **Rate-bursting devices** — the Phase 2 per-source rate limiter.

---

## What is genuinely not supported in v1

State this plainly in the User Guide rather than letting a customer discover it.

- **API-only cloud sources** — SaaS security products and cloud-managed platforms that
  expose logs only through a proprietary API. (Meraki works because it can send syslog to
  a local collector; a pure API source would need a connector, which is out of scope.)
- **NetFlow / sFlow / IPFIX** — flow data, not logs. Different product.
- **Endpoint agents** — no software is installed on monitored devices.
- **Encrypted proprietary formats** requiring vendor SDKs to decode.
