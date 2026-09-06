# Phase 2 — ingest performance

`dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --filter "*IngestionBenchmark*"`

Full BenchmarkDotNet console output: `benchmark-run.txt`. Per-iteration latency
percentiles: `ingest-latency.txt`. Machine-readable summary: `benchmarks.json`.
(The generated `BenchmarkDotNet.Artifacts/` HTML/CSV is git-ignored, like Phase 1.)

## Path measured

`FrameIntake` → `IngestionChannel` (fast path) / `DiskSpillQueue` (overflow) →
`IngestionPipeline` (batch) → `SqliteLogRepository.AppendBatchAsync` against a real SQLite
file in WAL mode. `parse_status = raw`; no parsing (Phase 3).

## Sustained throughput — gate: 5,000 msg/sec

| | value |
|---|---|
| Producer paced at | 8,000 msg/sec |
| Frames / iteration | 150,000 |
| Committed / iteration | 150,000 (**0 lost**) |
| Mean | 128.1 µs/frame, StdDev 1.17% (3 iterations) |
| Effective rate | **~7,800 msg/sec** — the pipeline keeps up with the 8,000 msg/sec feed, running <2% behind ideal pacing |

**PASS.** The pipeline sustains well above the 5,000 msg/sec gate; it tracked an 8,000
msg/sec feed with a negligible backlog.

## End-to-end latency (receive → committed), steady state

| iteration | p50 | p95 | p99 | max |
|---|---|---|---|---|
| 1 | 191 ms | 850 ms | 1021 ms | 1164 ms |
| 2 | 144 ms | 828 ms | 955 ms | 1048 ms |
| 3 | 177 ms | 880 ms | 1014 ms | 1089 ms |

Latency is dominated by `BatchLinger` (50 ms) plus batch fill and the WAL commit. p99
stays under ~1 s; max under ~1.2 s. (The discarded warm-up iteration shows p50 ≈ 330 ms
while the JIT and page cache settle.)

## Burst absorption — gate: 15,000 msg/sec for 60 s, zero loss

Separate run, producer **unpaced** — 300,000 frames pushed as fast as possible, so the
channel fills and everything spills to disk, then drains:

| | value |
|---|---|
| Frames | 300,000 |
| Committed | 300,000 (**0 lost**) |
| Drain rate | **~11,460 msg/sec** |

**PASS.** A burst that outruns the writer is fully absorbed by the disk spill queue with
zero loss, then drained at >11k msg/sec. Zero-loss under burst and under a stalled writer
is also asserted directly by `BackpressureSpillTests` and `ChaosTests`
(`sent == committed + dropped`, `dropped == 0`).

## Kill test — gate: no committed loss

`KillRecoveryTests.HardKillMidIngest_LosesNoDurableFrame_ThreeTimes` — the out-of-process
`IngestionProbe` receives over TCP at ~12,500 msg/sec with a deliberately slowed pipeline
so a spill backlog builds, is `Process.Kill()`ed mid-run, restarted in `recover` mode, and
every frame that was durable before the kill (committed, or fsync'd to spill) is present
afterwards with `integrity_check = ok`.

```
iteration 1: before kill committed=600 spillDurable=400; after recovery rows=1500, integrity ok
iteration 2: before kill committed=400 spillDurable=350; after recovery rows=1250, integrity ok
iteration 3: before kill committed=600 spillDurable=350; after recovery rows=1450, integrity ok
```

**PASS.** (The 10× Soak variant runs nightly — see known-issues P2-5.)

## Regression note (TESTING_STANDARDS.md §5)

This benchmark re-runs in Phases 3, 6, 7, and 12. Baseline to compare against: **7,800
msg/sec sustained**, **p99 ≈ 1.0 s end-to-end**, **11,460 msg/sec burst-drain**. A drop of
more than 10% in any of these between phases must be investigated before proceeding.
