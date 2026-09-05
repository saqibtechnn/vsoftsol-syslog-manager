# Phase 1 — Known issues

| # | Issue | Impact | Target |
|---|---|---|---|
| **P1-1** | **Insert benchmark marginally below the 20,000 rows/sec gate on virtualized hardware.** Measured 18.8 k (BenchmarkDotNet) / ~19.3 k (direct) on a VMware VM. The identical code + schema reaches ~23 k hand-bound and ~35 k with `synchronous=OFF` on the same box — the workload is fsync-bound on virtualized storage, and BenchmarkDotNet itself flags the VM. See `benchmarks.md`. | None for Phase 2: the write path sustains ~19 k/sec vs a 5 k/sec sustained target, and the Phase 2 disk spill queue absorbs bursts above write capacity. | Re-measured on the Phase 12 clean-VM acceptance run (BUILD_PLAN acceptance criterion 2), on bare-metal-class SSD. Also: a future `EventParameters` fast path (bulk timestamp formatting) can close the repo↔hand-bind gap. |
| P1-2 | **FTS search is eventually consistent** (ADR 0009). A just-ingested message is searchable after ≤ ~1 s (the `SearchIndexMaintainer` interval), not instantly. | Acceptable: Phase 5's "search < 2 s" is about query latency; async indexing matches Graylog/Elastic/Loki. Documented for the operator. | Phase 11 self-monitoring alerts if the indexing lag grows (maintainer stalled). |
| P1-3 | **Embedded NUL bytes in a message body are replaced with U+FFFD in the `message` / `search_text` columns** (`StorageFormat.SanitizeText`). SQLite reads TEXT back as a NUL-terminated C string, so an embedded NUL would silently truncate the stored value. | None to Constraint 4: `raw_message` (BLOB) keeps the true bytes verbatim — proven by `RepositoryFuzzTests`. | Documented; no change planned. |
| P1-4 | **`GetContextAsync` groups by `source_ip`, not hostname.** Network logs frequently have no hostname field or a spoofed one; the source address is the reliable "same host" key. | Behavioural choice, tested. | Phase 5 UI can offer a hostname-based grouping toggle if operators want it. |
| P1-5 | **`ConcurrencyTests` 60 s soak is opt-in** (`Category=Soak`). The per-commit run uses 8 s. | The CI per-commit run stays fast; the 60 s version runs nightly (TESTING_STANDARDS.md §7). | Nightly schedule wired in Phase 0's CI. |

Prior phase items still open: P0-1 (coverage gate — now enforceable from this phase, Data
at 90.6 %), P0-2 (Stryker runner env), P0-3 (CSP `unsafe-inline`).

No `TODO(phase-N):` markers in shipping code.
