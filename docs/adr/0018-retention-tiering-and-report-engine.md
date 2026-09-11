# ADR 0018 — Primary-stream retention tiering, a compressed-archive-with-database-side-hash tamper model, and reports reusing the search/aggregation path

**Status:** Accepted (Phase 10)

## Context

Without retention the database grows until the disk fills (CLAUDE.md Constraint 1: single
node, no external storage to overflow into). With it, the product needs a compliance
story: verifiable archives, and reports an Auditor can run without Administrator help.
Four decisions follow.

## Decision 1 — an event's *primary stream* owns its retention policy

An event can belong to several streams (`event_streams`), each with its own retention
override. Rather than "the most conservative policy across every stream" (correct but
expensive — no `event_id`-keyed index answers "the max of N joined rows" cheaply per
event without a window function per candidate row), the primary stream — the most
specific (lowest `stream_id`) **non-catch-all** stream the event belongs to, falling back
to the catch-all or the global default — owns the policy. Expressed as one indexed
`ROW_NUMBER() OVER (PARTITION BY event_id ORDER BY is_catch_all ASC, stream_id ASC)` CTE,
reused identically for the Hot→Warm and Warm→Cold queries. `hot_days` / `warm_days` /
`cold_days` are **tier durations**, not cumulative ages: Hot→Warm at age > `hot_days`,
Warm→Cold at age > `hot_days + warm_days`, archive purge at archive age > `cold_days`.

**Alternative rejected:** most-conservative-across-all-streams. More surprising in
practice too — an admin configuring a stream's retention expects that stream's setting to
apply to events primarily classified into it, not to be silently overridden by an
unrelated catch-all membership.

## Decision 2 — tamper evidence: hash in the database, never beside the file

Every archive gets a SHA-256 computed over the **complete compressed file**, recorded in
`archives.sha256` — a database row, not a sidecar file next to the archive. `RestoreArchiveAsync`
re-hashes and compares **before** calling `ArchiveFile.Parse` — a mismatch is refused
outright, never partially trusted, and the archive is marked `tamper_detected`. A
scheduled `SqliteArchiveVerifier` pass re-checks every archive that has gone stale
(default 7 days) independent of restore. **Documented honestly**: an attacker with *both*
filesystem and database write access could update both consistently; no software control
defends against that — only OS-level file integrity and backup controls can. This is the
same class of residual-risk disclosure the audit log's hash chain already makes.

## Decision 3 — the archive format has no internal paths, so zip-slip cannot exist by construction

Archives are one JSON header line + NDJSON event rows, compressed as a single tagged blob
(`CompressorFactory`: Zstd primary — ZstdSharp is a pure-managed port, no native binary,
no platform risk — Gzip fallback, both always registered for decompression so a
historical file stays readable regardless of which was primary when written). There is no
zip container and no internal file entry with its own path, so there is nothing to
zip-slip through. The only path-construction surface is the **archive's own filename**,
built from the stream name (admin-authored, not raw network input, but sanitised
defensively regardless — `ArchiveNaming.SanitizeSegment` strips separators and leading
dots) and re-verified with `IsSafeUnderRoot` immediately before every file write *and*
read. Decompression is bounded by a byte cap checked while streaming (`BoundedCopy`), not
by trusting a frame's self-reported size — the actual defence against a decompression
bomb, proved at both the compressor layer (a 50 MB-plaintext fixture, capped to 1 MB) and
the `RestoreArchiveAsync` pipeline layer (hash-then-cap, before any parsing).

## Decision 4 — reports reuse the Phase 5/9 query and aggregation paths; no bespoke query builder

A report is `{ template | (savedSearch | inlineQuery), timeRangeDays, schedule, delivery }`.
`ReportContentReader` resolves it through exactly the same two paths dashboards proved
scope-safe in Phase 9: `ScopedEventReader.SearchAsync` for list-shaped templates (Failed
Authentication Summary, Configuration Change Audit, Interface Flap), `SqliteAggregationReader
.AggregateAsync` for aggregate-shaped ones (Severity Trend, Top Talkers, Device
Availability) — plus one new source kind, `AuditLog`, for Rule & Alert Activity, since
rule/alert firings live in the audit trail, not the event store. **No new query path was
written for reports** — a report can never see more than the viewer's (or, for a
scheduled run, the owning user's) `UserScope` allows, proved by the same class of
cross-scope test Phase 9 used for dashboards, extended to cover the restore path (a
restored event stays scoped exactly as a live one). The 11 canned/compliance templates are
seeded as `is_system` rows (the `DefaultDashboards` idempotent-seed precedent), non-editable,
"Copy to schedule" produces an owned clone — the same shape as a dashboard's "Copy to edit".

**Correction made during this phase:** the query grammar's match-all convention is an
**empty string**, not `"*"` (a bare `"*"` is rejected — it is a prefix-wildcard operator,
not a wildcard token, per `SearchQueryParser`). Three canned templates originally used a
literal `"*"` and a validator/UI default did too; caught by the aggregation-oracle
integration test (which actually executes the query) rather than by review, and now
locked in with a unit test asserting no catalogue template uses a bare `"*"`.

## Cost accepted

- PDF/CSV row-count and figure correctness is proved at the `ReportContent`-vs-independent-SQL-oracle
  layer (`ReportContentReaderTests`), not by parsing the rendered PDF back out — QuestPDF
  has no reader API and OCR-testing a generated PDF would test the renderer's fidelity to
  data we already independently verified twice. The PDF/CSV tests prove rendering does not
  throw, scales with row count, and neutralises hostile content (script tags, CSV formula
  prefixes) without losing the underlying bytes in storage.
- Scheduled report delivery retries in-tick (bounded, immediate) rather than persisting a
  redelivery queue; a persistent failure surfaces as a Warning notification and an audit
  entry for an operator to act on, the same disposition as the alert-action dispatcher's
  bounded-retry-then-surface pattern (ADR 0016).
