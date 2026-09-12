# VSoftSol Syslog Manager — User Guide

For the day-to-day user: searching logs, building rules and alerts, dashboards, and
reports. For install/upgrade/backup and server administration, see the
[Admin Guide](ADMIN_GUIDE.md).

## Search

Search box syntax, from simplest to most specific:

- **Bare words** search the message text: `failed login`
- **A quoted phrase** matches exactly, punctuation and all: `"authentication failure for user root"`
- **`field:value`** targets one field. Leaving the box empty matches everything (not `*` —
  a bare `*` is rejected as an invalid wildcard operator, not treated as "match all").
- **A trailing wildcard** on a value: `host:web0*`
- **`AND`, `OR`, `NOT`**, and parentheses for grouping: `severity:error AND (host:web01 OR host:web02)`
- Range operators (`>`, `>=`, `<`, `<=`) work on fields marked "supports ranges" below:
  `severity:<=warning`, `received:>-1h` (the last hour), `event_id:>10000`

### Searchable fields

| Field | Aliases | Type | Ranges? |
|---|---|---|---|
| `message` | `msg`, `text` | free text | |
| `raw` | `raw_message` | free text (the untouched original bytes) | |
| `host` | `hostname` | text | |
| `source_ip` | `ip`, `src`, `src_ip`, `sourceip` | IP address | |
| `app` | `app_name`, `appname`, `program`, `tag` | text | |
| `proc_id` | `pid`, `procid` | text | |
| `msg_id` | `msgid` | text | |
| `severity` | `sev`, `level` | name or 0–7 (`emergency`…`debug`) | yes |
| `facility` | `fac` | syslog facility | yes |
| `vendor` | | text | |
| `protocol` | `proto` | `udp` / `tcp` / `snmp` / `wineventlog` | |
| `parse_status` | `parsed`, `status` | `parsed` / `raw` / `failed` | |
| `device` | `device_name` | reference to a registered device | |
| `device_id` | | number | yes |
| `stream` | `stream_name` | reference to a stream | yes |
| `event_id` | `id`, `event` | number | yes |
| `received` | `received_utc`, `time`, `received_time` | timestamp (when this server stored it) | yes |
| `event_time` | `event_utc`, `timestamp` | timestamp (the device's own timestamp, if present) | yes |
| `field.<name>` | | any extractor field you have defined (see below) | |

**Extracted fields**: if a message's log content has a piece of data you care about that
isn't one of the fields above (a source port, an ACL name, a session ID — anything), open
the **Pattern Tester** (Settings → Pattern tester) against a real sample of that message,
build a GROK or regex extractor with no code change or new release, save it, and it becomes
searchable as `field.<your name>` from then on for every future message that matches.

**Exporting results**: every search result set can be exported (CSV, JSON, or raw text) as
a streamed download from the search page. Values that could be interpreted as spreadsheet
formulas are neutralised on export only — the stored value is never altered.

## Rules and actions

A rule is a condition (the same field vocabulary as search, plus device-group scoping)
paired with one or more actions: send an email, call a webhook, or write to another stream.
Rules run against every message as it is ingested. An action that fails is retried with
backoff and, if it keeps failing, moved to a dead-letter view rather than silently dropped
or retried forever — check Settings → Rules → an individual rule's history if an action you
expect isn't happening.

## Alerts

An alert watches an **aggregation** over a time window (for example, "more than 50
`severity:error` events from `device_group:firewalls` in 5 minutes") rather than a single
message, and tracks open/acknowledged/resolved state so a spike doesn't re-notify on every
matching message. A silent device — one that stops sending anything at all — can itself be
the alert condition (a heartbeat check), which a single-message rule cannot express.

## Dashboards

A dashboard is a set of widgets, each one data source (a saved search or a system metric
like queue depth) plus an aggregation (count, distinct count, sum/avg/min/max, grouped or
not, bucketed over time or not) plus a visualisation (line, bar, counter, table, donut,
gauge, or a device status grid). Four are provided by default, including **Network
Overview**, which is where you land after the first-run wizard. Drag to rearrange; add a
widget in a guided 4-step picker (pick the data, pick the aggregation, pick the
visualisation, preview, add — five clicks). A shared dashboard's *definition* is shared, but
each viewer's own data-visibility scope still applies to what it shows them.

## Reports

Reports are generated from templates (including canned compliance templates such as a
90-day PCI-DSS report), can be run on demand or scheduled, and are delivered by email via a
configured SMTP profile. A generated report reflects your own visibility scope — an Auditor
role can produce a report covering everything they are permitted to see, archived data
included, without needing help from an administrator.

## Device Compatibility

*(This chapter mirrors `VENDOR_SUPPORT.md`, the same document the "Waiting for messages"
page draws its copy-ready commands from — the two can never drift, since one is generated
from the other.)*

### What "supported" means

| Tier | What it means | Coverage |
|---|---|---|
| **1 — Collection** | Received, stored, searchable, alertable. Zero configuration on our side. | **Every device that speaks RFC 3164 or RFC 5424.** Not a vendor list — the protocol. |
| **2 — Parsing** | Log content broken into named fields so rules and dashboards can target them precisely. | Ships with patterns for the vendors below. User-extensible with no code change (the Pattern Tester). |
| **3 — Non-syslog intake** | Sources that cannot send syslog at all. | SNMP traps (v1/v2c) and Windows Event Log. |

**No message is ever rejected for coming from an unrecognised vendor.** RFC 5424 is tried
first, then RFC 3164, then the message is stored raw (`parse_status:raw`) rather than
dropped. This is the single most important compatibility guarantee in the product.

### Vendors with a parsing pack today

Cisco IOS/IOS-XE · Cisco ASA · Fortinet FortiGate · Palo Alto PAN-OS · Juniper JunOS ·
MikroTik RouterOS · Ubiquiti UniFi · Linux (`sshd`, `sudo`, `cron`, `kernel`) · Check Point
Gaia · Sophos XG/XGS · SonicWall SonicOS · pfSense · OPNsense · Aruba (AOS-Switch and
AOS-CX) · Huawei VRP · VMware ESXi.

Every device configuration command for these (and where to point the ones without a
parsing pack yet) is on the "Waiting for messages" page you saw at first setup, and in
`VENDOR_SUPPORT.md` in the installation directory.

### Adding a new vendor's parsing

A pattern is a plain text file, loaded at runtime — adding one to
`Patterns\<vendor>\` is a file drop, not a new release. Use the Pattern Tester
(Settings → Pattern tester) against a real sample message from the device to build and
validate a pattern before saving it.

### Not supported in v1 — stated plainly

- **API-only cloud log sources** that do not emit syslog at all (a vendor's REST/webhook
  log API) — out of scope; this product is a syslog/SNMP-trap/Windows-Event-Log collector,
  not a general log-shipping platform.
- **NetFlow / sFlow / IPFIX** flow-record protocols — a different data model from syslog
  messages; not collected.
- **Endpoint agents** — there is no lightweight forwarder agent to install on servers or
  workstations; those need their own OS-native syslog forwarding (e.g. `rsyslog` on Linux,
  NXLog on Windows) pointed at this server, or the built-in Windows Event Log intake for
  Windows hosts specifically.

If one of these is a hard requirement for you, it is v1.1-or-later scope, not a
configuration you are missing — please tell your VSoftSol Syslog Manager contact so it can
be prioritised.
