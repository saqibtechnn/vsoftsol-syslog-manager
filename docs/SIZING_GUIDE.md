# VSoftSol Syslog Manager — Sizing Guide

How much CPU, RAM, and disk to give this server, and worked examples for three common
deployment sizes. The retention math here is the exact calculation the first-run wizard's
retention step and Settings → Retention use (`RetentionEstimator` /
`RetentionPresets`) — these numbers and what the product shows you will never disagree.

## Throughput

Measured on this project's own 2-vCPU development VM (`docs/evidence/phase-11/benchmarks.md`),
which is a conservative baseline, not a target machine:

| Workload | Throughput |
|---|---|
| RFC 3164/5424 parsing only | 23,139 messages/sec |
| Full pipeline, all 17 vendor parser packs loaded | 7,329 messages/sec |

Real deployments rarely approach either number sustained — these are the ceiling the
ingest pipeline itself can process; your actual load is a function of device count and
message rate, below. Give the collector more CPU if you are running close to these ceilings
sustained, not just during a burst.

## The assumption behind every number below

There is no universal "typical" syslog rate — a quiet switch and a firewall under attack
differ by orders of magnitude. Every figure in this guide (and in the wizard's retention
step) assumes a planning baseline of **5 events/minute/device** and an **average stored
message size of 300 bytes** (`message` + `raw_message` combined) — a reasonable
steady-state assumption, not a hard limit. An incident will burst well above it; the ingest
pipeline and its disk-backed spill queue absorb bursts far larger than steady-state without
losing a message (Constraint 3), but sustained load meaningfully above this assumption
should size up a tier.

## RAM and CPU

- **RAM**: budget roughly 2 GB for the application itself (Kestrel, the Blazor Server UI,
  the in-process ingest channel) plus SQLite's page cache, which scales with hot-tier
  database size. 4 GB total is comfortable for Small, 8 GB for Medium, 16 GB for Large.
- **CPU**: 2 vCPUs comfortably covers Small and Medium at the assumed rate (both are far
  below the 7,329 msg/sec measured ceiling even accounting for parsing overhead). Large's
  83 events/sec assumed rate is also well within that ceiling on 2 vCPUs, but give it 4
  vCPUs if you also run heavy concurrent dashboard/report/search usage on the same box —
  those share the same process (`docs/adr/0005-single-service-over-split-processes.md`).
- **Disk**: SSD strongly recommended for the data directory — SQLite's write-ahead log and
  the hot tier's query patterns are latency-sensitive. Spinning disk is workable for the
  Cold tier's archive files specifically (sequential, infrequently read) if you relocate
  the archive root to separate, slower storage (Settings → Retention).

## Worked examples

Each preset's Hot/Warm/Cold day counts are the exact defaults the wizard offers; every
example uses the assumed rate above (5 events/min/device, 300 bytes/event, and the
product's default 0.35 assumed compression ratio for Warm/Cold storage, which is what a
brand-new database's estimate uses before it has real observed data to measure).

### Small — up to ~50 devices (a single site or branch office)

Retention: 30 days hot / 60 days warm / 180 days cold.

| | |
|---|---|
| Assumed load | 250 events/min ≈ 360,000 events/day ≈ 4.2 events/sec |
| Hot tier (30 days, uncompressed) | ≈ 3.0 GB |
| Warm tier (next 60 days, compressed) | ≈ 2.1 GB |
| Cold/archive tier (next 180 days, compressed) | ≈ 6.3 GB |
| **Total disk at steady state** | **≈ 11.5 GB** |

### Medium — up to ~200 devices (a campus or mid-size network)

Retention: 30 days hot / 90 days warm / 365 days cold.

| | |
|---|---|
| Assumed load | 1,000 events/min ≈ 1,440,000 events/day ≈ 16.7 events/sec |
| Hot tier (30 days, uncompressed) | ≈ 12.1 GB |
| Warm tier (next 90 days, compressed) | ≈ 12.7 GB |
| Cold/archive tier (next 365 days, compressed) | ≈ 51.4 GB |
| **Total disk at steady state** | **≈ 76.1 GB** |

### Large — up to ~1,000 devices (a large enterprise or multi-site estate)

Retention: 14 days hot / 90 days warm / 730 days (2 years) cold.

| | |
|---|---|
| Assumed load | 5,000 events/min ≈ 7,200,000 events/day ≈ 83.3 events/sec |
| Hot tier (14 days, uncompressed) | ≈ 28.2 GB |
| Warm tier (next 90 days, compressed) | ≈ 63.4 GB |
| Cold/archive tier (next 730 days, compressed) | ≈ 514.0 GB |
| **Total disk at steady state** | **≈ 605.5 GB** |

Large's cold tier dominates total size — if 2-year retention is a compliance requirement
rather than a preference, plan disk for it specifically; if it is not required, a shorter
cold-tier window (Settings → Retention, per-stream or global) reduces total disk
proportionally with no other effect on the running system.

## Once you have real traffic

These are planning numbers for a server that has never received a message. Once real
traffic is flowing, Settings → Retention shows the **measured** estimate (actual observed
events/day, actual average message size, actual observed compression ratio) instead of
this guide's assumptions — trust that number over this guide once it exists.
