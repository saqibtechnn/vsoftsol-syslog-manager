# ADR 0010 — Ingestion durability model and the disk spill queue

**Status:** Accepted (Phase 2)

## Context

CLAUDE.md Constraint 3 ("never lose a message") governs the receive path. Ingestion must
survive writer stalls, disk pressure, and a hard process kill, while sustaining 5,000
msg/sec and absorbing 15,000 msg/sec bursts (BUILD_PLAN.md / PHASE_02). ADR 0003 already
fixed the mechanism: an in-process `System.Threading.Channels` hand-off backed by a disk
spill queue. This ADR pins down the durability contract and the spill queue's design,
because "our code to get right" (ADR 0003) needs to be written down.

## Decision

### The acceptance / durability contract

A frame moves through three states:

1. **Accepted** — taken off the wire and either written to the in-memory channel or
   appended to the spill queue. `FrameIntake` is the single choke point.
2. **Durable** — either committed to SQLite (Phase 1's WAL guarantee: survives `kill -9`),
   or written to a spill segment that has been force-flushed to disk
   (`Flush(flushToDisk: true)`).
3. **Committed** — a row in `events`.

**Guarantee:** no *durable* frame is ever lost. A hard kill can lose only frames that were
accepted-but-not-yet-durable — i.e. sitting in the in-memory channel, or in the
sub-flush-interval tail of the spill segment (≤ `SpillFlushInterval`, default 100 ms, of
traffic). This residual window is inherent to any design that does not `fsync` per
datagram, and is acceptable for connectionless UDP syslog, which has no delivery
acknowledgement in the first place. It is documented in the Phase 12 hardening guide.

Graceful shutdown closes this window: on stop, listeners stop accepting, the pipeline is
given `ShutdownDrainTimeout` to commit everything, and if that is exceeded the remaining
in-memory frames are flushed to the spill queue (durable) before exit — so a *clean* stop
loses nothing.

### The spill queue

- **Append-only segment files** (`seg-NNNNNNNN.dat`), length-prefixed records
  (`FrameCodec`). One writer (listeners, serialised), one reader (the pipeline).
- **Group-commit fsync:** a background loop forces buffered writes to disk every
  `SpillFlushInterval` and on every segment roll. `DurableFrameCount` tracks how far the
  durable boundary has advanced.
- **Drain with a persisted cursor:** the pipeline *leases* a batch of durable frames,
  commits them to the database, then advances a cursor file (`cursor.dat`, fsync'd)
  past them. A segment is deleted only after the cursor is durably past its end.
- **At-least-once, never at-most-once:** a crash between the database commit and the
  cursor fsync re-delivers those frames on the next start. Duplicate rows on crash
  recovery are accepted; de-duplication is explicitly out of scope for v1 (PHASE_02
  "Do not build ... deduplication").
- **Torn-tail recovery:** only the newest segment can be mid-write. On start it is scanned
  record-by-record and truncated to the last whole record; a corrupt record partway
  through stops the scan with everything before it intact.
- **Hard size cap** (`SpillMaxBytes`, default 4 GiB): once reached, further frames are
  dropped **with a counter and an error-level alert** rather than filling the disk
  (SECURITY_STANDARDS.md §2). Frames already on disk are unaffected. This is the only
  path — other than an explicitly configured rate-limit `Drop`/`Quarantine` — by which the
  collector discards an accepted frame, and it is always attributed.

### `events.listener_id`

Phase 2 stores `listener_id` as NULL. Linking events to a persisted `listeners` row needs
the listener-management surface, which is Phase 4 (Constraint 7: configuration lives in
the database and is edited from the UI). Per-listener counters in Phase 2 are keyed by the
configured listener name in the in-memory stats service, not by a database id.

## Alternatives rejected

- **`fsync` per frame:** ~4 k frames/sec on the dev VM (the same wall Phase 1 hit with a
  per-row FTS trigger, ADR 0009). Fails the throughput gate.
- **Everything through the spill log (WAL-style), channel as a read-ahead buffer:** clean,
  but the phase prompt's model is "channel primary, spill on overflow", and an
  overflow-only spill keeps the common-case path allocation-free and off disk entirely.
- **Unbounded in-memory queue:** a denial-of-service vector (ADR 0003).
- **Exactly-once via a committed-offset table keyed per frame:** the bookkeeping cost is
  not worth it when duplicates on crash are harmless and dedup arrives as a feature later.

## Consequences

- Search/rules/reporting must tolerate rare duplicate rows after an unclean restart until
  a dedup feature lands. None of them assume uniqueness today.
- The spill directory needs its own ACL (service account only) — folded into the Phase 12
  installer work alongside the database ACL (ADR 0006).
- `benchmarks.md` records the measured sustained throughput and end-to-end latency
  percentiles; the ingest benchmark re-runs in Phases 3/6/7/12 (TESTING_STANDARDS.md §5).
