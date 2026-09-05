# ADR 0001 — SQLite over embedded PostgreSQL

**Status:** Accepted (Phase 0)

## Context

The product is single-node, one installer, no external runtime dependencies
(CLAUDE.md Constraints 1–2). It must ingest ≥ 5,000 msg/sec, keep raw bytes forever,
and support full-text search over the message body.

## Decision

Use **SQLite** (with the FTS5 extension) as the only datastore. Access it behind the
`ILogRepository` seam so a server-class database can replace it later.

## Alternatives rejected

- **Embedded PostgreSQL** (e.g. a bundled `postgres` binary): a real server process to
  supervise, a data directory to initialise, port and socket management, version
  upgrades, and a much larger installer. Contradicts "one installer, no external runtime
  dependencies".
- **LiteDB / embedded document stores:** weaker query story, no mature FTS, small
  ecosystem for the 2 a.m. maintainer.
- **RavenDB / LiteDB / Elasticsearch embedded:** explicitly out of scope (Constraint 2).

## Cost accepted

- Single-writer model: all writes serialise through one connection / WAL. Mitigated by
  batching in the ingestion path (Phase 2) and shown to hold 5,000 msg/sec by benchmark.
- No network access to the database; backup is a file copy with WAL checkpoint.
- Large-retention deployments lean on the archival tier (Phase 10) rather than one
  ever-growing hot file.
- If a customer outgrows single-node, the migration is a new `ILogRepository`
  implementation plus a data export, not a rewrite.
