# ADR 0003 — System.Threading.Channels over an external message broker

**Status:** Accepted (Phase 0)

## Context

Ingestion must absorb 15,000 msg/sec bursts and never lose a committed message, including
across a hard process kill (CLAUDE.md Constraint 3). No external runtime dependencies
(Constraint 2).

## Decision

In-process **`System.Threading.Channels`** as the bounded hand-off between listeners and
the database writer, backed by a **disk spill queue** (Phase 2) for durability under
writer stalls, disk pressure, and crash.

## Alternatives rejected

- **RabbitMQ / Redis / Kafka:** each is a separate service to install, supervise, secure,
  and upgrade. Directly contradicts Constraint 2, and none is needed at single-node
  scale.
- **A plain `BlockingCollection` / unbounded queue:** unbounded memory growth under load
  is a denial-of-service vector (SECURITY_STANDARDS.md §2 "Resource exhaustion").
- **Writing every datagram straight to SQLite:** cannot sustain the burst target; couples
  network receipt latency to disk latency.

## Cost accepted

- Durability is our code to get right (the spill queue and its fsync discipline), proven
  by the Phase 2 hard-kill test rather than delegated to a broker.
- No cross-process or cross-node fan-out — by design (single node).
