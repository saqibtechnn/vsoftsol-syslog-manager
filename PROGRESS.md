# PROGRESS

Claude Code updates this file at the end of every phase. A fresh session reads it first
to learn where the build stands. Keep it terse and factual.

---

## Current state

- **Last completed phase:** 3 — Parsing
- **Last tag:** `v1.0.0-phase.3`
- **Next phase:** 4 — UI & auth
- **Build status:** green — `dotnet build -c Release` warning-clean, `dotnet test` 452/452 (0 skipped, Soak excluded), `dotnet format` clean, SCA clean (13 projects)
- **Branding:** `branding/logo.png` present — yes (788 KB); `branding/brand.json` present; `branding/placeholder/logo.png` committed
- **Insert benchmark:** 1M batched insert = **18,781 rows/sec** (Phase 1, MARGINAL vs 20k — I/O-bound on the VMware dev VM; re-verify Phase 12).
- **Ingest benchmark:** Phase 2 burst-drain **~11,460 msg/sec**. Phase 3: RFC-parse pipeline **~13,600 msg/sec** (no regression, +19%); full parse + vendor extraction **~5,300 msg/sec** worst case (200k unique msgs all matching the busiest pack, 2-vCPU VM) — clears the 5,000 gate marginally; pure parse+extract **~105,000 msg/sec**. See `docs/evidence/phase-03/benchmarks.md`.

---

## Phase log

<!-- Append one block per completed phase. Newest at the top. -->

### Phase 3 — Parsing & Normalization — 2026-09-06 — tag `v1.0.0-phase.3`

> Proceeded in the same session as Phase 2 at the operator's explicit direction ("proceed"),
> against the one-phase-per-session default. Two evidence gates are environmentally blocked
> and were substituted / carried (see sign-off): the live rsyslog/syslog-ng oracle (no
> Docker/WSL on this host — substituted with an independent regex-reference parser, 0
> divergences) and Stryker mutation testing (P0-2, runner does not deploy on this
> SDK-only host — config complete, carried to CI).

**Shipped** (`src/VSoftSol.Syslog.Ingestion/Parsing/`, `Extraction/`, `Patterns/`)
- `Rfc5424Parser` — full header + structured data (multi-element, escaped values) → JSON;
  hand-written span parser, no regex on the header path; returns false for non-5424.
- `Rfc3164Parser` — BSD timestamp with year inference + 31 Dec/1 Jan rollover, optional
  Cisco sequence number, sub-second time, optional explicit year (ASA), optional `TZ:`
  token; lenient but still falls through to `raw` for unstructured input.
- `MessageParser` facade — decode → 5424 → 3164 → `raw`; never throws; `raw` keeps
  `source_ip` / `received_utc` / `raw_message`; the **wire `source_ip` always wins** over a
  claimed hostname; meta fields (`truncated`, `timestamp_ambiguous`, `framing_anomaly`,
  `charset`). Raw events linked to the **Parse Failures** system stream by the repository.
- `PayloadDecoder` — UTF-8 BOM / UTF-16 BOM detection, strict UTF-8 → latin-1 fallback,
  `MaxMessageChars` cap with a `truncated` field (raw bytes always kept in full).
- Extractor pipeline — `GrokLibrary` (~120-line GROK subset, `Compiled` + mandatory match
  timeout), `GrokExtractor` / `RegexExtractor` / `KeyValueExtractor` / `JsonExtractor`
  (depth-capped) / `CsvExtractor` (per-type `when` guards) / `LookupExtractor` /
  `TransformExtractor`; each stage isolated (a bad pattern never stops ingestion); field
  count + value length caps (`ExtractionContext`, `fields_truncated` marker).
- **Vendor packs — the core eight**, plain text, runtime-loaded (**ADR 0011**):
  `cisco-ios`, `cisco-asa`, `fortigate` (KV), `paloalto` (positional CSV per log type),
  `juniper-junos`, `mikrotik-routeros`, `ubiquiti-unifi`, `linux` (sshd/sudo/cron/kernel).
  `PatternPackLoader` (malformed pack → log + skip, never fatal), `VendorExtractor`
  (first-match-wins by priority).
- `DeduplicationWindow` — identical host + message within N seconds bumps
  `occurrence_count` via `ILogRepository.IncrementOccurrenceAsync` instead of a new row;
  default 0 (off). In-memory, per-process.
- Wired into `IngestionPipeline` (replaces the Phase-2 raw mapper). `ParsingOptions`
  (`Parsing` section); `VendorExtractionEnabled` toggle. Server GC on the Service host.
- 200 committed fixtures (`tests/fixtures/messages/<vendor>/corpus.jsonl`, 25/vendor) with
  field-by-field expected output.

**Verification output** (`docs/evidence/phase-03/`)
- `dotnet build -c Release` → 0 warnings, 0 errors
- `dotnet test` (Soak excluded) → **452 passed, 0 failed, 0 skipped** (was 148)
- `dotnet format --verify-no-changes` → exit 0
- 200/200 fixtures parse to their expected fields; `raw_message` byte-identical for 100%
- Malformed suite (truncated PRI, no timestamp, oversized, invalid UTF-8, empty) → stored
  `raw`, linked to Parse Failures, never an exception
- Year-rollover (Dec-in-Jan, Jan-in-Dec) → correct year
- Dedup → `occurrence_count` increments, no duplicate row
- Property (FsCheck, ≥ 22k cases) + fuzz (1 MB, 10k SD, ANSI, NUL, ReDoS bait) → 0
  exceptions, 0 hangs, raw byte-identical every time; the ReDoS match timeout fires
- **Independent oracle** — 150 fixtures, **0 divergences** on every RFC header field
  (`oracle-comparison.md`)
- Log-forging matrix (CRLF, fake PRI, forged hostname, NUL) → exactly one event, source IP
  authoritative, hostile payloads stored byte-identical
- Memory bounds — field / value / SD caps enforced, all bounded < 5 s
- **Phase 2 chaos suite re-run with parsing inline → 7/7** (chaos ×10, kill ×10, fuzz 1M)
- SCA clean, 13 projects; **no new dependency**
- Coverage: **Ingestion 89.5 %** line (union of both suites; gate ≥ 80 % — PASS)
- Benchmark: pure parse+extract ~105,000 msg/sec; end-to-end full ~5,300 msg/sec worst
  case (2-vCPU VM), RFC-only ~13,600 msg/sec (`benchmarks.md`, `benchmarks.json`)

**Decisions made**
- **ADR 0011** — vendor packs are plain text, loaded at runtime; INI-like format; malformed
  → skip; first-match-wins by priority; mandatory regex timeout + field caps; the core
  eight shipped with 200 fixtures. A third-party GROK NuGet was rejected (dependency on the
  ingest path + SCA gate); a ~120-line subset covers the core eight.
- The parse fallback chain **never rejects a message** (Constraint 4) — validates PRI
  range / 5424 version / timestamp format and falls to `raw` otherwise.
- **No sanitisation on ingest** — hostile payloads stored byte-identical; an explicit
  passing test guards it so no later phase "fixes" it.
- The wire-observed `source_ip` is authoritative over any hostname in the message.
- `RegexOptions.Compiled` + match timeout for pack patterns (NonBacktracking measured
  ~40 % slower on the fast-matching common case; kept only as the fallback).
- Server GC enabled on the Service host (allocation-heavy concurrent workload).
- Added `ILogRepository.IncrementOccurrenceAsync` (dedup) — same seam, more surface.
  `SqliteLogRepository.AppendBatchAsync` hard-wires the one route (`parse_status='raw'` →
  Parse Failures stream); the general routing engine is Phase 6.

**Sign-off block** (TESTING_STANDARDS.md §9)
```
PHASE 3 SIGN-OFF
  Tests added:            ~110 unit (RFC parsers, decoder, extractors, GROK, pack loader,
                          dedup, property, fuzz, log-forging, memory bounds, oracle) +
                          200 fixture theory cases + 5 integration. ~315 total new.
  Total suite:            452 tests, 452 passing, 0 skipped (Soak run nightly:
                          chaos x10, kill x10, wire-fuzz 1M -> 7/7 green with parsing inline)
  Red-green observed:     yes (docs/evidence/phase-03/red-green.md) - fixture corpus and
                          RFC-compliance tests written first; 6 real defects found
                          test-first, each with a permanent regression test
  Coverage:               Ingestion 89.5% line (union of both suites; gate >= 80% - PASS).
                          Data still 90.5%. Rules/Reporting gate N/A (Phase-0 shells).
  Mutation score:         BLOCKED on this host (P0-2 / P3-3 - Stryker VsTest adapter does
                          not deploy). Config complete + targeted (parser/extractor/pack).
                          Carried to a CI host. Not a FAIL - environmentally blocked, and
                          the parser's test depth (RFC compliance + 200 fixtures + property
                          + fuzz + independent oracle) is strong.
  Performance gates:      parsing must not drop sustained ingest below 5,000 msg/sec:
                          pure parse+extract ~105,000 msg/sec (PASS, 20x).
                          full pipeline end-to-end, worst case (200k unique msgs all
                          matching the busiest pack, 2-vCPU VMware VM): ~5,100-5,900 msg/sec
                          across 4 runs -- MARGINAL PASS (clears 5,000 every run).
                          RFC-parse-only pipeline ~13,600 msg/sec -- FASTER than the Phase 2
                          baseline of 11,460, so RFC parsing added NO regression.
                          The 20x standalone-vs-pipeline gap is a documented investigation
                          item (P3-2), re-verified on Phase 12 clean-VM hardware. Mitigation
                          shipped: VendorExtractionEnabled toggle + per-source rate limiter.
  Oracle / differential:  live rsyslog/syslog-ng not run - no Docker/WSL on this host (P3-1).
                          Substituted with an independent regex-reference parser: 150
                          fixtures, 0 divergences on every RFC header field. Live run added
                          to the Phase 12 checklist.
  UX gate:                N/A - no screen shipped
  Regression:             all Phase 0/1/2 tests green - yes (148 -> 452, none changed);
                          Phase 2 chaos suite re-run 7/7 with parsing inline
  Evidence committed:     docs/evidence/phase-03/
  Security gate:          log-injection/forging PASS / source-IP-authority PASS /
                          no-sanitisation-on-ingest PASS / ReDoS timeout PASS /
                          memory-exhaustion caps PASS / fuzz (>= 32k cases, 0 crash/hang)
                          PASS / oracle differential PASS / raw-retention 100% PASS /
                          SCA PASS / SAST PASS
  Open findings:          0 C, 0 H, 0 M, 1 L (P0-3, carried). No new residual risks.
```

**Deferred**
- [ ] P3-1: live oracle vs rsyslog/syslog-ng — Phase 12 checklist (needs a container host).
- [ ] P3-2: investigate the pipeline parse-cost inflation; re-verify perf on Phase 12 hardware.
- [ ] P3-3: run Stryker on a CI host with the VsTest adapter.
- [ ] Re-run the ingest benchmark in Phases 6, 7, 12.

**Known issues** — `docs/evidence/phase-03/known-issues.md` (P3-1 … P3-6).

### Phase 2 — Ingestion core — 2026-09-06 — tag `v1.0.0-phase.2`

**Shipped** (`src/VSoftSol.Syslog.Ingestion/`)
- `UdpSyslogListener` — `Socket` UDP, large `SO_RCVBUF`, `ArrayPool` receive buffer, Windows
  `SIO_UDP_CONNRESET` suppressed, always receives the whole datagram then trims to
  `MaxMessageBytes` with a `truncated` flag (never drops — VENDOR_SUPPORT.md).
- `TcpSyslogListener` — per-connection read loop + `SyslogStreamFramer` (newline **and**
  RFC 6587 octet-counted framing, auto-detected from the first byte); connection cap,
  idle timeout, half-open / reset tolerated.
- `IngestionChannel` — bounded `Channel<RawFrame>`, `BoundedChannelFullMode.Wait`,
  `TryWrite` fast path.
- `DiskSpillQueue` — append-only length-prefixed segments (`FrameCodec`), background
  group-fsync, persisted+fsync'd drain cursor, delete-segment-only-after-commit,
  torn-tail recovery, hard `SpillMaxBytes` cap (drop-with-counter + alert). **ADR 0010.**
- `FrameIntake` — the single choke point: rate decision → channel → (full) spill queue.
- `PerSourceRateLimiter` — token bucket per source IP, injected `TimeProvider`;
  throttle / drop-with-counter / quarantine; disabled by default (Constraint 3).
- `IngestionPipeline` — drains channel + spill, batches, commits `parse_status=raw` via
  `ILogRepository`; spills in-memory frames back to disk on commit failure / shutdown
  overrun (never loses an accepted frame).
- `IngestionHostedService` — startup spill replay **before** listeners open; graceful
  drain on stop bounded by `ShutdownDrainTimeout` (overrun flushes to disk, logs).
- `IngestionStatistics` / `IngestionStatsSnapshot` — per-listener + per-source counters
  (received / queued / spilled / recovered / committed / dropped / throttled / failed);
  the load-test ledger identity `received == committed + dropped + failed + in-flight`.
- `IngestionOptions` bound from the `Ingestion` config section; `AddSyslogIngestion` +
  `AddCollectorRuntime(config)` wiring. Removed the Phase-0 no-op `CollectorHostedService`.
- `tests/VSoftSol.Syslog.IngestionProbe` — out-of-process host for the hard-kill test.
- ADR 0010; THREAT_MODEL B1 rows updated; ASVS V1.11 / V11.1 updated; SECURITY_REVIEW
  P2-R1 / P2-R2 (accepted residual risks).

**Verification output** (`docs/evidence/phase-02/`)
- `dotnet build -c Release` → 0 warnings, 0 errors
- `dotnet test` (Soak excluded) → **148 passed, 0 failed, 0 skipped** (was 99)
- `dotnet format --verify-no-changes` → exit 0
- 100k UDP → 100k committed, ledger balanced; 100k TCP newline + 100k TCP octet-counted
  → committed, ledger balanced
- Backpressure: repository stalled → frames spill to disk, 0 lost, drain clean on recovery
- **Kill test** (`Process.Kill()` mid-ingest ×3): every durable frame present after
  recovery, `integrity_check = ok` — passed 5/5 consecutive runs
- Rate-limit: throttle / drop / quarantine behaviours + counters match config (virtual clock)
- Wire fuzz (20k fast): no crash / hang / socket leak
- Flood (10× limit ×100 sources): stays up, shed-with-counter, disk bounded, legit source ingested
- Slowloris (2,000 half-open): connection cap holds, UDP unaffected
- Benchmark: sustained ~7,800 msg/sec vs 5,000 gate (PASS); burst-drain ~11,460 msg/sec;
  p50 ~175 ms / p95 ~850 ms / p99 ~1.0 s end-to-end (`benchmarks.md`, `benchmarks.json`)
- SCA clean, 13 projects (`security/sca-vulnerable.txt`)
- Coverage: **Ingestion 87.0%** line (gate ≥ 80% — PASS); Data still 90.6%
- **Soak variants** (chaos ×10 both scenarios, kill ×10, wire-fuzz 1M) — **7/7 green**
  (`soak-run.txt`); they run on the nightly schedule per TESTING_STANDARDS.md §7. The
  PHASE_02 "chaos suite passes 10/10" gate is met.

**Decisions made**
- **ADR 0010** — the ingest durability contract (accepted / durable / committed) and the
  spill queue design. Durable = committed to SQLite, or fsync'd into a spill segment. A
  hard kill loses only not-yet-durable frames (in-memory channel or ≤ `SpillFlushInterval`
  spill tail) — inherent to a non-per-message-fsync design and to unacked UDP; accepted.
- Spill recovery is **at-least-once** — a crash between DB commit and cursor fsync
  re-delivers frames. Duplicates on crash are accepted (dedup is out of scope, PHASE_02).
- `events.listener_id` is stored NULL until the Phase 4 listener-management UI exists
  (Constraint 7); per-listener counters are keyed by listener name in the stats service.
- Rate limiting defaults **off**; when on, defaults to **Throttle** (no loss). `Drop` /
  `Quarantine` are explicit operator choices for abuse and always increment a counter.
- Raw-only mapping fabricates `facility=user`, `severity=notice` (RFC 5424 §6.2.1 no-PRI
  default, PRI 13); Phase 3's parser replaces this.
- Removed `CollectorHostedService` (Phase-0 no-op); `AddCollectorRuntime` now wires the
  real ingestion stack and takes `IConfiguration`.
- `CA1711` suppressed for the Ingestion project — `DiskSpillQueue` is the name PHASE_02
  prescribes; the `-Queue` suffix is the domain term.

**Sign-off block** (TESTING_STANDARDS.md §9)
```
PHASE 2 SIGN-OFF
  Tests added:            20 unit (FrameCodec, SyslogStreamFramer, PerSourceRateLimiter),
                          29 integration (UDP, TCP, spill queue, backpressure, rate limit,
                          lifecycle, wire fuzz, security/flood/slowloris, chaos, kill).
                          49 total new.
  Total suite:            148 tests, 148 passing, 0 skipped  (Soak variants run nightly:
                          chaos x10, kill x10, wire-fuzz 1M)
  Red-green observed:     yes  (docs/evidence/phase-02/red-green.md) — component knock-outs
                          + 2 real defects found test-first (D2-1 oversized-UDP loss,
                          D2-2 shutdown hang), each with a permanent regression test
  Coverage:              Ingestion 87.0% line (gate >= 80% — PASS); Data still 90.6%.
                          Rules/Reporting gate still N/A (Phase-0 shells).
  Mutation score:         N/A  (Stryker — first real target is the Phase 3 parser, P0-2)
  Performance gates:      sustained ingest 7,800 msg/sec vs 5,000 target — PASS.
                          burst-drain 11,460 msg/sec, zero loss vs 15,000 burst target — PASS
                          (zero-loss under burst/stall asserted directly by the ledger).
                          end-to-end latency p50 ~175 ms / p99 ~1.0 s.
                          kill test — PASS x3 (5/5 consecutive).
  UX gate:                N/A — no screen shipped
  Regression:             all Phase 0 + Phase 1 tests green — yes (99 -> 148, none changed)
  Chaos suite:            10/10 on every scenario (kill x10, repo-stalled x10,
                          disk-full x10) + wire-fuzz 1M — soak-run.txt, PASS
  Evidence committed:     docs/evidence/phase-02/
  Security gate:          availability-under-flood PASS / malformed-frame fuzz PASS (0
                          crashes) / slowloris PASS / spill disk-full graceful PASS /
                          hard-kill durability PASS / bind-address PASS / rate-limit
                          correctness PASS / raw-bytes-preserved PASS / SCA PASS / SAST
                          PASS. Spill-file ACL deferred to the Phase 12 installer (ADR 0006).
  Open findings:          0 C, 0 H, 0 M, 1 L (P0-3, carried). 2 accepted residual risks
                          (P2-R1 UDP spoofing, P2-R2 kill loss-window) — design properties,
                          not defects.
```

**Deferred**
- [ ] P2-1: link `events.listener_id` to a persisted `listeners` row — Phase 4.
- [ ] P2-2: spill / segment / cursor file ACLs — Phase 12 installer (ADR 0006).
- [ ] P2-5: harden the kill-probe Soak (×10) launch further if nightly CI shows flakiness.
- [ ] Re-run the ingest benchmark in Phases 3 / 6 / 7 / 12 (TESTING_STANDARDS.md §5).

**Known issues** — `docs/evidence/phase-02/known-issues.md` (P2-1 … P2-7).

### Phase 1 — Data layer — 2026-09-06 — tag `v1.0.0-phase.1`

> Operator decision on the insert-benchmark gate: **tagged** with the performance line
> marked MARGINAL (I/O-bound on the VMware dev VM — 23k hand-bound / 35k `synchronous=OFF`
> on the same box; the deferred-FTS code fix took it 3.4k → 18.8k). Re-verified on the
> Phase 12 clean-VM acceptance run (BUILD_PLAN acceptance criterion 2). All other gates PASS.

**Shipped**
- `Microsoft.Data.Sqlite 8.0.30`; `SqliteConnectionFactory` — WAL, `synchronous=NORMAL`,
  `busy_timeout`, `foreign_keys=ON`, `wal_autocheckpoint=10000`, and a process-wide
  single-writer `SemaphoreSlim` (`AcquireWriteLockAsync`).
- `MigrationRunner` — forward-only, versioned, SHA-256-checksummed embedded `.sql`;
  idempotent; drift (an applied script edited in place) throws.
- `Migrations/Scripts/001_initial.sql` — `events` (canonical schema, `AUTOINCREMENT`),
  `event_fields`, `event_streams`, `devices`, `device_groups`, `device_group_members`,
  `listeners`, `streams`, `rules`, `users`, `roles`, `audit_log` (append-only via
  `BEFORE` triggers), `fts_state`; `events_fts` FTS5 (external content, one `search_text`
  column, IP/MAC tokenchars); indexes on `received_utc`, `source_ip`, `severity`,
  `device_id`, `event_fields(name,value)`, `hostname`, `audit_log`.
- `SqliteLogRepository : ILogRepository` — batched transactional inserts (default 500,
  configurable), `GetById`, streaming `QueryAsync` with a parameterised filter builder,
  `CountAsync`, `GetContextAsync` (±N from the same `source_ip`), plus
  `PurgeOlderThanAsync` (chunked, FTS-consistent), `SyncSearchIndexAsync`,
  `RebuildSearchIndexAsync`, `CheckpointAsync`.
- `SearchIndexMaintainer` (`BackgroundService`) — deferred FTS indexing off the ingest
  path (**ADR 0009**); watermark in `fts_state`; passive WAL checkpoint when caught up.
- `DatabaseSeeder` — 4 roles, seeded `admin` (password set by the Phase 12 wizard),
  7 default streams; idempotent.
- `DatabaseInitializer` (`IHostedService`, first) — migrate + seed before anything runs.
- `AddSyslogData` + composition-root wiring; `SqliteDataOptions` bound from config,
  `DatabasePath` derived from the collector data directory.
- `tests/VSoftSol.Syslog.CrashProbe` — helper exe for the 20× WAL kill test.
- ADR 0009 (deferred FTS indexing). THREAT_MODEL B3 + ASVS rows updated.

**Verification output** (`docs/evidence/phase-01/`)
- `dotnet build -c Release` → 0 warnings, 0 errors
- `dotnet test` → **99 passed, 0 failed, 0 skipped** (Soak trait excluded per §7); the
  20× hard-kill WAL consistency test green
- `dotnet format --verify-no-changes` → exit 0
- `dotnet run … Benchmarks -- --filter "*Insert*"` → 18,781 rows/sec mean, 3 runs,
  StdDev 1.0% (`benchmarks.md`, `benchmark-run.txt`)
- SCA clean, 12 projects (`security/sca-vulnerable.txt`)
- Coverage: **Data 90.6 %**, overall 85.3 % (`coverage-summary.txt`)

**Decisions made**
- **ADR 0009** — FTS is synced by a background maintainer, not an `AFTER INSERT` trigger:
  a per-row trigger measured at 4.3k rows/sec (4× under the gate); deferral gets the
  ingest path to ~19k and FTS catches up at ~45k. Search is eventually-consistent
  (≤ ~1 s).
- `event_id` is `AUTOINCREMENT` (monotonic, never reused) so a single watermark suffices.
- `GetContextAsync` groups by `source_ip` (reliable) not `hostname` (often absent/spoofed).
- Embedded NUL in text columns → U+FFFD (`StorageFormat.SanitizeText`); `raw_message`
  BLOB keeps the true bytes (Constraint 4), proven by fuzz tests.
- `wal_autocheckpoint` raised to 10,000 pages (~40 MB) — the 1,000-page default makes
  almost every commit pay a main-DB fsync.
- `CA2100` lowered to advisory (flags all structural SQL); `SCS0002` taint analysis is
  the enforced SQL-injection build gate.
- Added join tables (`device_group_members`, `event_streams`) and `fts_state` beyond the
  literal Phase-1 table list — they complete the listed tables' semantics and save a
  migration; user↔stream scope tables deferred to Phase 4.

**Sign-off block** (TESTING_STANDARDS.md §9)
```
PHASE 1 SIGN-OFF
  Tests added:            ~11 unit (StorageFormat, migration loading, + Phase-0 still green),
                          ~28 integration (repository, migration, FTS, constraints,
                          concurrency, retention, seed, security sweep, fuzz, property,
                          WAL crash, maintainer)
  Total suite:            99 tests, 99 passing, 0 skipped  (Soak trait run nightly)
  Red-green observed:     yes  (docs/evidence/phase-01/red-green.md)
  Coverage:               Data 90.6% line; overall 85.3%. The §3 80% gate on
                          Ingestion/Rules/Reporting is still N/A (Phase-0 shells).
  Mutation score:         N/A  (Stryker runner env — known-issues P0-2; first real
                          target is the Phase 3 parser)
  Performance gates:      1M batched insert: 18,781 rows/sec (bench) / ~19,300 (direct)
                          vs 20,000 target — **MARGINAL MISS, I/O-bound on the VMware
                          dev VM**. Same code = 23k hand-bound, 35k synchronous=OFF.
                          The 5.5x code fix (deferred FTS, 3.4k -> 18.8k) is done; the
                          residual is environmental. Re-verified on Phase 12's clean-VM
                          acceptance run (BUILD_PLAN acceptance criterion 2). See
                          benchmarks.md.  --> operator accepted; tagged with this line
                          marked MARGINAL and a Phase 12 re-verification commitment.
  UX gate:                N/A — no screen shipped
  Regression:             all Phase-0 tests green — yes
  Evidence committed:     docs/evidence/phase-01/
  Security gate:          SQL-injection sweep PASS / parameterisation PASS / fail-closed
                          PASS / no-payload-in-logs PASS / WAL crash consistency PASS /
                          constraint matrix PASS / SCA PASS. DB-file ACL deferred to the
                          Phase 12 installer (ADR 0006).
  Open findings:          0 C, 0 H, 0 M, 1 L (P0-3, carried)
```

**Deferred**
- [ ] P1-1: re-measure the insert benchmark on Phase 12 clean-VM hardware.
- [ ] DB / WAL / journal file ACLs — Phase 12 installer (ADR 0006).
- [ ] user↔stream / user↔device-group scope tables — Phase 4.

**Known issues** — see `docs/evidence/phase-01/known-issues.md` (P1-1 … P1-5).

### Phase 0 — Architecture & skeleton — 2026-09-05 — tag `v1.0.0-phase.0`

**Shipped**
- `VSoftSol.Syslog.sln` with the exact CLAUDE.md layout: `Core`, `Data`, `Ingestion`,
  `Rules`, `Reporting` (class libs), `Service` (Windows Service host / composition root),
  `Web` (Blazor Server), plus `UnitTests`, `IntegrationTests`, `Benchmarks`, and the
  build-only `build/VSoftSol.Syslog.BrandingGen`.
- `Directory.Build.props` / `.targets` / `Directory.Packages.props`: .NET 8, C# 12,
  nullable on, `TreatWarningsAsErrors`, deterministic + `ContinuousIntegrationBuild`,
  central package management (no floating versions), shared `1.0.0` version.
- `Core`: canonical event schema (`SyslogEvent`, `EventField`, `SyslogPriority`), enums
  (`Facility`, `Severity`, `Protocol`, `ParseStatus`, `Role`), and the two seams —
  `ILogRepository`, `IAuthenticationProvider` — fully specified, unimplemented. No I/O.
- Layer shells wired inward-only (each references `Core`, verified by fitness tests).
- `Service`: generic-host bootstrap, `AddWindowsService`, Serilog → rolling file +
  Windows Event Log, no-op `CollectorHostedService`, `AddSyslogPlatform` composition root.
- `Web`: Blazor Server, HTTPS-only, baseline security headers middleware, one placeholder
  page reading `BrandingInfo`, shares the `Service` DI registrations, `UseAntiforgery`.
- **Branding pipeline** (ADR 0007): `BrandingGen` console tool derives every omitted asset
  from `branding/logo.png` with ImageSharp, generates `BrandingInfo.g.cs` + `brand.css`,
  missing logo → warning + placeholder (never a build failure), byte-identical on rebuild.
- `.editorconfig` (security analyzer rules elevated to errors), `.gitignore`,
  `.gitattributes`, `.config/dotnet-tools.json`, `stryker-config.json`, `.gitleaks.toml`.
- `.github/workflows/ci.yml`: build (warnings=errors), format, test+coverage, SCA
  (fails on any vuln) + deprecated, CodeQL SAST, Gitleaks, CycloneDX SBOM, arch + literal
  gates, reproducible-build check — each shown to fail on violation.
- 8 ADRs (`docs/adr/0001`–`0008`), `docs/security/THREAT_MODEL.md` (STRIDE × 5
  boundaries), `docs/security/ASVS-checklist.md` (ASVS 5.0 L2 baseline),
  `docs/security/SECURITY_REVIEW.md`.

**Verification output** (`docs/evidence/phase-00/`)
- `dotnet build -c Release` → Build succeeded, 0 Warning(s), 0 Error(s) (`build-output.txt`)
- `dotnet test` → 32 passed, 0 failed, 0 skipped (`test-output.txt`)
- `dotnet format --verify-no-changes` → exit 0
- `dotnet run --project src/VSoftSol.Syslog.Web` → HTTPS placeholder page HTTP 200, CSP +
  security headers present, `branding/brand.css` served
- Reproducible build: two clean builds → byte-identical assemblies (`reproducibility.txt`)
- Rebrand acceptance: swap `logo.png` + `brand.json` → derived assets and `BrandingInfo`
  change, **zero source edits** (`rebrand-acceptance.txt`)
- Deliberate-failure proofs for all four gates (`deliberate-failures.txt`)
- SCA clean (`security/sca-vulnerable.txt`)

**Decisions made**
- ADR 0001 SQLite over embedded Postgres · 0002 Blazor Server over SPA · 0003 Channels
  over external broker · 0004 Zstd over gzip · 0005 one service, not split processes ·
  0006 least-privilege service account + data-dir ACLs · 0007 branding pipeline + scope
  of the literal guard · 0008 exactly two seams.
- Roles: `Administrator`, `Operator`, `ReadOnly`, `Auditor` (PHASE_04).
- Literal guard targets brand **values** (product/vendor name, URLs, colours), not the
  mandated `VSoftSol.Syslog.*` namespace token — see ADR 0007. Rebrand acceptance proves
  Constraint 11's intent is met.
- `Core` takes a `ReferenceOutputAssembly="false"` project reference on `BrandingGen`
  for build ordering only; creates no IL dependency (fitness test verifies).
- ImageSharp pinned to the 2.1.x (Apache-2.0) line, not 3.x (split licence); 2.1.13
  clears the advisories that affected earlier 2.1.x.
- Transitive pins added for legacy `System.Net.Http` / `System.Security.Cryptography.*`
  dragged in by old test-only analyzers, to keep SCA clean.

**Sign-off block** (TESTING_STANDARDS.md §9)
```
PHASE 0 SIGN-OFF
  Tests added:            24 unit (incl. 2 property, 5 architecture/layering, 8 branding),
                          4 integration
  Total suite:            32 tests, 32 passing, 0 skipped
  Red-green observed:     yes  (docs/evidence/phase-00/red-green.md)
  Coverage:               ~57% overall (BrandingGen 87%, Core partial); threshold gate
                          deferred to Phase 1 — Ingestion/Rules/Reporting are shells
                          (known-issues P0-1)
  Mutation score:         N/A  — Stryker wired + configured; runner env issue on this
                          host, no mutable domain logic yet (known-issues P0-2)
  Performance gates:      N/A for Phase 0
  UX gate:                N/A — no functional screen shipped (placeholder page only;
                          first real UI is Phase 4)
  Regression:             N/A — first phase; full suite green
  Evidence committed:     docs/evidence/phase-00/
  Known issues:           5, listed in known-issues.md
  Security gate:          SAST (analyzers local + CodeQL in CI) PASS / SCA PASS /
                          secrets (manual + Gitleaks in CI) PASS / DAST N-A (no auth UI)
  Open findings:          0 C, 0 H, 0 M, 1 L   (P0-3: CSP unsafe-inline on style-src,
                          fixed in Phase 4)
```

**UX gate**: N-A — Phase 0 ships only the unstyled placeholder page explicitly permitted
by the phase prompt; the five-point gate applies from Phase 4.

**Deferred**
- [ ] P0-1: enforce line-coverage ≥ 80% on Ingestion/Rules/Reporting — from Phase 1.
- [ ] P0-2: get Stryker completing a run (VsTest adapter env) — Phase 3, first real target.
- [ ] P0-3: remove CSP `'unsafe-inline'` (`style-src`) with nonces — Phase 4.

**Known issues**
- See `docs/evidence/phase-00/known-issues.md` (P0-1 … P0-5). No `TODO(phase-N)` markers
  in shipping code.

---

## Open decisions needing the operator

- **P0-3 sign-off** (Low): accept CSP `'unsafe-inline'` on `style-src` until Phase 4?
  Recorded in `docs/security/SECURITY_REVIEW.md` awaiting operator initials.
- **Environment**: the build machine had no .NET SDK; .NET 8.0.424 was installed to
  `%USERPROFILE%\.dotnet` (user-local, added to user PATH). WiX (Phase 12) is not yet
  installed.

---

## Deferred items across all phases

| Marker | Where | Target phase |
|---|---|---|
| _(none — no `TODO(phase-N)` in code)_ | | |
| P0-3 CSP nonces | `SecurityHeadersMiddleware` | 4 |
| P2-1 `events.listener_id` link + listener-management UI | `MessageParser`, `SqliteLogRepository` | 4 |
| P2-2 spill / segment / cursor file ACLs | Phase 12 installer | 12 |
| P3-1 live oracle vs rsyslog/syslog-ng | `OracleDifferentialTests` | 12 (container host) |
| P3-2 pipeline parse-cost investigation + perf re-verify | `IngestionPipeline` / `VendorExtractor` | 12 |
| P3-3 Stryker mutation run (was P0-2) | `stryker-config.json` | CI host with VsTest adapter |
| P1-1 re-measure insert benchmark on clean-VM hardware | `benchmarks` | 12 |
| Re-run ingest throughput benchmark | `IngestionBenchmark` | 6, 7, 12 |

_(P0-1 coverage gate met — Ingestion 89.5%, Data 90.5%.)_
