# Phase 3 — known issues

| ID | Severity | Issue | Plan |
|---|---|---|---|
| P3-1 | info | Live oracle testing against rsyslog/syslog-ng was not run — no Docker/WSL on this host. Substituted with an independent regex-based reference parser (150 fixtures, 0 divergences — `oracle-comparison.md`). | Phase 12 pre-release checklist, on a host with a container runtime. |
| P3-2 | low (perf) | Vendor extraction on the ingest hot path: pure parse+extract is ~9.6 µs/msg (~105k msg/sec) but the full pipeline worst case (200k unique messages all matching the busiest pack, 2-vCPU VM) is ~5,300 msg/sec — clears the 5,000 gate marginally. The ~20× standalone-vs-pipeline gap is unexplained; likely GC pressure from the spilled-frame decode→parse→field-list allocation chain with only 2 GC-capable cores. | Re-verify on Phase 12 clean-VM hardware (more cores, no pump/producer contention). A perf pass (pool `ExtractionContext` / field lists, reduce spill re-decode allocs) is a candidate for a later phase. Mitigation shipped: `Parsing:VendorExtractionEnabled = false` (13,600 msg/sec) + the Phase 2 per-source rate limiter. |
| P3-3 | info | Stryker.NET still cannot complete a run on this SDK-only host (P0-2, since Phase 0 — the VsTest adapter does not deploy). Config is complete and targets the parser + extractor + pack parser. | Run on a CI host with the VsTest adapter. The parser's test depth (RFC compliance, 200 fixtures, property, fuzz, oracle) makes a strong score likely but it is not yet proven. |
| P3-4 | info | JunOS `sshd`/`kernel` messages **without** a JunOS event tag are not vendor-attributed to `juniper-junos` (the linux pack may claim them) — generic daemon names are deliberately excluded from the juniper match rules so a plain Linux host is not mis-attributed. Messages are still fully parsed and stored. | Accepted. A hostname-based match can be added when the device inventory (Phase 6) exists. |
| P3-5 | info | RFC 3164's optional Cisco sequence number is consumed but not stored as a field when it is followed by a space rather than `": "` (some IOS variants); the `%FACILITY-…` mnemonic is still extracted. | Minor; the vendor pack's `cisco_seq` GROK group covers the common `"N: "` form. |
| P3-6 | info | The dedup window is in-memory and per-process; a restart resets it (documented in ADR — dedup is a de-noiser, not a correctness guarantee). Within-batch duplicates on the *first* batch each insert a row; later identical messages fold. | Accepted for v1 (`DeduplicationWindow` default 0 = off). |

Carried from earlier phases: P0-3 (CSP `unsafe-inline` — Phase 4), P1-1 (insert benchmark
re-measure — Phase 12), P2-1 (`events.listener_id` link + listener UI — Phase 4), P2-2
(spill file ACLs — Phase 12).
