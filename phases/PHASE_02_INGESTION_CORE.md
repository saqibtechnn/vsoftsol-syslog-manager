# PHASE 2 — Ingestion Core

## Context
Phase 1 gave you a working repository with batched inserts. This phase builds the
receive path. **Constraint 3 in CLAUDE.md governs everything here: never lose a
message.** This is the highest-risk phase in the build.

## Objective
UDP and TCP listeners feeding a bounded channel and a disk-assisted spill queue, proven
to lose nothing under burst load and under a hard process kill.

## Build
1. `UdpSyslogListener` — configurable bind address and port (default 514), high
   receive-buffer size, no per-message allocation in the hot path (use
   `ArrayPool<byte>`).
2. `TcpSyslogListener` — default 514, supporting **both** newline-delimited framing and
   RFC 6587 octet-counted framing, auto-detected per connection. Per-connection
   read loop, connection limit, idle timeout.
3. `IngestionChannel` — bounded `System.Threading.Channels` channel with a configurable
   capacity and `BoundedChannelFullMode.Wait`.
4. `DiskSpillQueue` — when the channel is at capacity, write raw frames to an append-only
   on-disk segment file with a length-prefixed format and an fsync policy. Drain back
   into the channel when depth drops below a low-water mark. Segments are deleted only
   after their contents are committed to the database.
5. `IngestionPipeline` background service — drains the channel, batches, commits through
   `ILogRepository`. At this phase, store messages with `parse_status = raw`; Phase 3
   adds parsing.
6. **Graceful shutdown**: on stop, stop accepting, drain the channel and spill queue,
   commit, then exit. Bounded by a configurable timeout that is logged if exceeded.
7. **Startup recovery**: on start, replay any spill segments left by a previous run
   before opening listeners.
8. Per-listener and per-source counters: received, queued, spilled, committed,
   dropped, failed. Exposed via an internal stats service.
9. Per-source rate limiting with configurable ceiling and breach behaviour
   (throttle / drop-with-counter / quarantine).

## Do not build in this phase
Parsing, TLS, SNMP, Windows Event Log intake, deduplication, UI. Those are Phases 3 and 11.

## Tests to write first
- 100,000 messages over UDP → assert 100,000 committed.
- 100,000 messages over TCP, both framing modes → assert 100,000 committed.
- Backpressure test: stall the repository, assert messages spill to disk and none are lost.
- **Kill test**: send at 5,000 msg/sec, `Process.Kill()` mid-run, restart, assert every
  message acknowledged before the kill is present after recovery.
- Rate-limit test: assert breach behaviour matches configuration and the counter increments.

## Verification — run these and paste output
```bash
dotnet test tests/VSoftSol.Syslog.IntegrationTests --filter "Ingestion"
dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --filter "*Ingest*"
```
Assert and record: sustained 5,000 msg/sec, burst 15,000 msg/sec for 60 s with zero
loss. **Do not proceed to Phase 3 until the kill test passes.**

## Validation & Evidence (per `TESTING_STANDARDS.md`)

This is the highest-risk phase. Validation here is deliberately adversarial.

- **Accounting reconciliation** — every load test asserts `sent == committed + dropped`,
  with `dropped` explicitly zero for the zero-loss tests. Counting stored rows is not
  enough; the ledger must balance.
- **Chaos suite**, each repeated 10 times to catch races:
  - `kill -9` mid-ingest at 5,000 msg/sec → restart → assert zero committed loss
  - Disk full during spill → assert graceful degradation and a clear alert, never corruption
  - Repository stalled 60 s → assert spill engages and drains cleanly
  - Half-open TCP connections, connection floods, abrupt client resets
- **Fuzz the wire** — random bytes, zero-length datagrams, 64KB datagrams, split TCP
  frames, interleaved framing modes, 10k concurrent connections. Nothing may crash the
  listener or leak a socket.
- **Soak test — 24 hours at 2,000 msg/sec.** Assert: memory flat (< 5% growth after
  warm-up), no handle or socket leak, no queue-depth drift, no throughput decay.
  Nightly CI job, not per-commit.
- **Performance, measured properly** — three runs, mean and standard deviation, report
  p50/p95/p99 end-to-end latency alongside throughput. Averages hide the failures.
- **Evidence:** load-test ledgers, chaos matrix (10 runs × 4 scenarios), 24h soak memory
  graph, `benchmarks.json`.

**Do not proceed to Phase 3 until the chaos suite passes 10/10 on every scenario.**

## Security Validation (per `SECURITY_STANDARDS.md`)

The listener is unauthenticated and reachable by anyone with network access. Treat it as
an internet-facing service even though it is not one.

- **Availability as a security property** — flood at 10× the rate limit from 100 spoofed
  source IPs. Assert: the service stays up, the disk does not fill, legitimate sources are
  still ingested, and the event is alerted rather than silently absorbed.
- **Malformed frame fuzzing** — 1M generated hostile frames (random bytes, zero-length,
  64KB, split framing, mixed framing on one connection, null bytes, invalid priorities).
  Assert no crash, no hang, no unbounded allocation, no socket leak.
- **Slowloris / connection exhaustion** — hold 10,000 half-open TCP connections. Assert
  the connection cap and idle timeout hold and UDP ingestion is unaffected.
- **Spoofing acknowledgement** — document explicitly that UDP source IPs are unverifiable,
  record it as accepted residual risk in the threat model, and note TLS with mutual
  authentication as the mitigation for customers who need it.
- **Bind-address assertion** — listeners bind only where configured. A test asserts the UI
  never binds `0.0.0.0` by default.
- **Evidence:** flood test results, fuzz corpus and crash count (must be zero), connection
  exhaustion results, threat model residual-risk entry.

## Definition of Done
Standard DoD, plus both throughput numbers and the kill-test result in PROGRESS.md.

## Commit
`feat: phase 2 — udp/tcp listeners, bounded channel, disk spill queue` → tag `v1.0.0-phase.2`
