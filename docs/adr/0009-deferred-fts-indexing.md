# ADR 0009 — Deferred full-text indexing, not a sync trigger

**Status:** Accepted (Phase 1)

## Context

PHASE_01 asks for an FTS5 virtual table over the message text "kept in sync by triggers"
and sets a hard gate: the 1,000,000-row batched insert must exceed **20,000 rows/sec**,
because Phase 2's sustained (5,000/sec) and burst (15,000/sec for 60 s) targets sit on
top of the write path.

Measured on the dev environment (SQLite via `Microsoft.Data.Sqlite`, WAL,
`synchronous=NORMAL`, the full 19-column `events` schema with the four mandated indexes):

| Configuration | Rows/sec |
|---|---|
| Insert only, no FTS | ~19–23k |
| Insert + **`AFTER INSERT` trigger** into `events_fts` | **~4.3k** |
| Insert + the *same* `INSERT INTO events_fts` issued directly from the repository | ~16–17k |
| Insert + FTS populated **after the fact** in bulk | ~44–47k |

A per-row trigger is ~10× slower than the identical statement run directly — SQLite
re-enters the trigger program per row and does not amortise it across the transaction.
Even a direct per-row FTS insert on the hot path leaves no headroom for Phase 2.

## Decision

**The ingest path writes only the event store. Search indexing is deferred to a
background service.**

- `events.event_id` is `INTEGER PRIMARY KEY AUTOINCREMENT` — strictly increasing, never
  reused — so a single watermark is sufficient.
- `fts_state.last_indexed_event_id` records how far indexing has progressed.
- `SqliteLogRepository.SyncSearchIndexAsync(maxRows)` copies the next window of rows into
  `events_fts` with one set-based `INSERT … SELECT` and advances the watermark, in one
  transaction under the write lock.
- `SearchIndexMaintainer` (a `BackgroundService`) calls it on a short interval (default
  1 s), looping immediately while it keeps filling its batch, and runs a passive WAL
  checkpoint when it catches up.
- Retention (`PurgeOlderThanAsync`) deletes the corresponding `events_fts` entries in the
  same transaction as the row delete, so the index never drifts.
- `RebuildSearchIndexAsync` (`'rebuild'`) is available for repair.

**Consequence:** a just-ingested message becomes searchable after at most one maintainer
interval (≈ 1 s). This matches how Graylog / Elastic / Loki index asynchronously and is
well within Phase 5's "search < 2 s" requirement, which is about query latency, not
indexing latency.

WAL tuning: `wal_autocheckpoint` is raised from the 1,000-page default to 10,000
(≈ 40 MB). At the default, checkpoints fire so often under load that nearly every commit
pays a main-database fsync; 10,000 keeps the WAL bounded while letting sustained writes
run several times faster. The maintainer's passive checkpoint keeps it tidy when idle.

## Alternatives rejected

- **`AFTER INSERT` trigger (as the phase prompt suggests):** 4.3k rows/sec — misses the
  gate by 4×. The phase's implementation hint is incompatible with its own performance
  gate; this ADR is the "smallest change" that satisfies both (CLAUDE.md rules of
  engagement).
- **Direct per-row FTS insert on the ingest path:** ~16k/sec — still below 20k, no
  Phase 2 headroom.
- **`content=''` (contentless) FTS:** loses the ability to `'rebuild'` and to return
  snippets from the index; external content is the right trade.
- **A second datastore for search (Lucene.NET, Tantivy):** violates "no external runtime
  dependencies" and "SQLite only".

## Cost accepted

- Eventual consistency of search results (≤ ~1 s), documented for the operator.
- One more `BackgroundService` and a watermark row to reason about.
- If the maintainer stops, search goes stale (but ingestion and the raw store are
  unaffected); self-monitoring in Phase 11 alerts on a growing indexing lag.
