# VSoftSol Syslog Manager — Master Build Plan

How to run this build with Claude Code, and what each phase delivers.

---

## How to run it

1. Create an empty git repository. Copy `START_HERE.md`, `CLAUDE.md`,
   `UX_STANDARDS.md`, `BRANDING.md`, `VENDOR_SUPPORT.md`, `TESTING_STANDARDS.md`,
   `SECURITY_STANDARDS.md`, `BUILD_PLAN.md`, `PROGRESS.md`, `phases/`, and `branding/`
   into the root.
2. Drop your `logo.png` into `branding/` and edit `brand.json` if you want to change the
   product or vendor strings. Nothing else is required — see `BRANDING.md`.
3. Open Claude Code in that directory and say: **"Read START_HERE.md and begin."**
4. It reads the controller, checks `PROGRESS.md` for the next phase, opens that phase
   prompt, and runs the Explore → Plan → Test → Code → Verify → Commit loop.
5. When it prints "Phase N complete and tagged", **clear the context** and start a fresh
   session with the same sentence. It picks up from PROGRESS.md automatically.
6. If a phase goes wrong: `git reset --hard v1.0.0-phase.<N-1>`, then re-run with a note
   describing what failed.

Never run two phases in one context window. Context bloat is the main cause of quality
collapse in long agentic builds.

---

## Why it is structured this way

| Practice | Reason |
|---|---|
| `CLAUDE.md` at root | Auto-loaded every session; constraints survive context clears |
| One phase per session | Keeps context small, which is the single biggest driver of output quality |
| Runnable verification commands | A prose gate can be claimed as passed; a command cannot |
| Tests written before code | Forces the requirement to be pinned down before implementation drifts |
| Git tag per phase | Cheap rollback point when a phase goes sideways |
| `PROGRESS.md` | The handoff file — a fresh session reads it and knows exactly where it is |
| Explicit "do not touch" list per phase | The main defence against scope creep |
| `UX_STANDARDS.md` + a per-phase UX gate | Turns "make it user friendly" into something that can actually be failed |
| `TESTING_STANDARDS.md` + committed evidence packs | A model will claim a gate passed; a committed benchmark file and a recorded red-to-green transition cannot be claimed |
| `SECURITY_STANDARDS.md` + a per-phase security gate | The product ingests hostile input by design and shows it to admins — security cannot be a final hardening pass |
| Fixed file layout given up front | Stops Claude inventing a different structure each session |

---

## Phase map

| Phase | Name | Ships | Tag |
|---|---|---|---|
| 0 | Architecture & skeleton | Solution, projects, seams, ADRs, CI | `v1.0.0-phase.0` |
| 1 | Data layer | Schema, migrations, FTS5, repositories | `v1.0.0-phase.1` |
| 2 | Ingestion core | UDP/TCP listeners, bounded channel, disk spill queue | `v1.0.0-phase.2` |
| 3 | Parsing & normalization | RFC 3164/5424, raw retention, parse-failure path | `v1.0.0-phase.3` |
| 4 | UI shell & auth | Blazor host, HTTPS, RBAC, audit log | `v1.0.0-phase.4` |
| 5 | Search | Query language, FTS5 search, live tail, context view, export | `v1.0.0-phase.5` |
| 6 | Devices & streams | Registry, discovery queue, groups, stream routing | `v1.0.0-phase.6` |
| 7 | Rules & actions | Filter→action engine, all action types, rate limits | `v1.0.0-phase.7` |
| 8 | Aggregation alerts | Threshold, distinct-count, device-silent heartbeat, ack/resolve | `v1.0.0-phase.8` |
| 9 | Dashboards | Widget framework, default dashboards | `v1.0.0-phase.9` |
| 10 | Retention, archive & reports | Tiering, Zstd, SHA-256 archive verification, report engine | `v1.0.0-phase.10` |
| 11 | Extras & hardening | TLS, SNMP traps, Windows Event Log, config bundles, MFA, self-monitoring | `v1.0.0-phase.11` |
| 12 | Packaging & release | MSI, first-run wizard, docs, clean-VM acceptance run | `v1.0.0` |

If you need to ship sooner, the two safe cuts are Phase 9 (dashboards — search covers
the need) and the compliance report templates inside Phase 10. Everything else is
load-bearing.

---

## Feature provenance

The product deliberately combines patterns from five established tools:

| Capability | Pattern source | Phase |
|---|---|---|
| Multi-protocol listeners, disk-backed queues, zero loss | rsyslog / syslog-ng | 2, 11 |
| Filter→action rules engine, SNMP traps, unified config | Kiwi Syslog Server NG | 7, 11 |
| Streams, pipeline extractors, aggregation alerts | Graylog | 3, 6, 8 |
| Compliance report packs, archive hashing, scheduling | ManageEngine EventLog Analyzer | 10 |
| Retention tiering, normalized field schema, widgets | Elastic Stack | 9, 10 |

The three features that actually differentiate this product from cheap collectors, and
which must not be cut: **context view** (±N messages around any event from the same
host), **device-silent heartbeat alerts**, and **storing unparseable messages instead
of dropping them**.

There is a fourth, and against the competition it may be the strongest: **setup that
does not require expertise**. Graylog and Elastic need a stack and a specialist. Kiwi
makes you configure the same thing twice in two consoles. EventLog Analyzer is heavy.
A product a two-person IT team installs in ten minutes and understands without training
wins on that basis alone — which is why `UX_STANDARDS.md` is a binding constraint and
not a wish list.

---

## Canonical event schema

Every phase from 3 onward depends on this. It does not change without an ADR.

```
event_id            INTEGER PK
received_utc        TEXT (ISO-8601 UTC)   -- when the collector got it
event_utc           TEXT (ISO-8601 UTC)   -- timestamp from the message, nullable
source_ip           TEXT
hostname            TEXT
app_name            TEXT
proc_id             TEXT
msg_id              TEXT
facility            INTEGER (0-23)
severity            INTEGER (0-7)
protocol            TEXT (udp|tcp|tls|snmp|wineventlog)
listener_id         INTEGER FK
message             TEXT                  -- parsed body
raw_message         BLOB                  -- original bytes, ALWAYS populated
parse_status        TEXT (rfc5424|rfc3164|raw)
occurrence_count    INTEGER default 1
structured_data_json TEXT nullable
device_id           INTEGER FK nullable
vendor              TEXT nullable
```

Extracted custom fields go in `event_fields(event_id, name, value)` — indexed, so new
vendor patterns never require a schema migration.

---

## Performance targets — asserted by benchmarks, not assumed

| Metric | Target | Verified in |
|---|---|---|
| Sustained ingest | 5,000 msg/sec | Phase 2 |
| Burst | 15,000 msg/sec for 60 s, zero loss | Phase 2 |
| Zero loss on hard kill mid-ingest | 0 committed messages lost | Phase 2 |
| Search, 30-day hot window, ≤1,000 rows | < 2 s | Phase 5 |
| Dashboard load | < 3 s | Phase 9 |
| Idle memory | < 400 MB | Phase 12 |

---

## v1 acceptance criteria

1. Clean Windows Server VM → installer → receiving live syslog from a Cisco switch in
   under 10 minutes, with no manual config-file editing.
2. All performance targets met with committed benchmark output.
3. Killing the service mid-ingest at 5,000 msg/sec loses zero committed messages.
4. Every setting is changeable from the web UI; no second console, no config file.
5. An Auditor-role user produces a 90-day PCI-DSS logging report covering archived data
   without Administrator help.
6. Every unparseable message is stored, searchable by raw text, and visible in the
   Parse Failures stream.
7. The audit log is a complete, ordered, tamper-evident record of every config change
   made during acceptance testing.
8. Line coverage ≥ 80% on Ingestion, Rules, and Reporting.
9. **A network admin who has never seen the product installs it, configures a Cisco
   switch to send to it, and finds a specific log message — using only the UI, with no
   documentation and no assistance.** This is a real test with a real person, not a
   thought experiment.
10. Every primary task (search, add a device, create a rule, create an alert, run a
    report) is reachable in **5 clicks or fewer** from the dashboard, and completable
    keyboard-only.
11. Every screen passes the five-point UX gate in `UX_STANDARDS.md`.

---

## Out of scope for v1 — do not build, do not abstract for

Clustering or HA · multi-tenant SaaS · full SIEM correlation · threat-intel feeds ·
NetFlow/sFlow/IPFIX · ML or LLM anomaly detection · endpoint agents · mobile apps ·
cloud or internet-facing deployment.
