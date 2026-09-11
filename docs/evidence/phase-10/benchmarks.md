# Phase 10 — benchmarks

## Retention tiering throughput (`RetentionBenchmark`)

The phase's acceptance gate — "tiering a 10M-event backlog does not push search latency
past the Phase 5 target while it runs" — needs a 10M-event seed plus a concurrent
search-latency measurement; on this 2-vCPU VMware build VM that is a multi-hour,
noise-dominated run (the established P1-1 / P5-1 / P6-1 / P9-1 pattern — see
`dev-vm-constraints` memory). This isolates the component instead: real per-batch
throughput for the Hot→Warm compression stage.

```
$ dotnet run -c Release --project tests/VSoftSol.Syslog.Benchmarks -- --filter "*Retention*"
retention benchmark dataset: 200,000 events, all eligible

| Method                           | Mean     | Error | P50      | P95      |
|---------------------------------- |---------:|------:|---------:|---------:|
| 'Hot -> Warm, 5,000-event batch' | 344.8 ms |    NA | 344.8 ms | 344.8 ms |
```

5,000 events compressed (Zstd level 3) and written back per batch in ~345 ms —
**~14,500 events/sec** for the compression stage alone, on one thread, with the process
also fresh off a 200k-row cold seed. Every tiering query (Hot→Warm select, Warm→Cold
select, the archive-export group query) is bounded by `RetentionSettings.BatchSize` and
runs against indexes already proven by Phase 1/5 (`event_id`, `received_utc`), so it
cannot itself starve a concurrent search's index usage — the literal 10M-backlog
concurrent-search-latency acceptance is carried to the Phase 12 clean-VM run (**P10-1**),
the same disposition already accepted for P1-1 / P5-1 / P6-1 / P9-1.

`BenchmarkDotNet` environment warning (expected, every phase): "Benchmark was executed on
the virtual machine with VMware hypervisor. Virtualization can affect the measurement
result." Run in isolation per the dev-VM constraint (nothing else building/testing
concurrently) — see `dev-vm-constraints` memory for why that matters on this host.

## No ingest-path change

Retention tiering and the report scheduler are both scheduled `BackgroundService`s off the
ingest path in the collector host (`git diff --stat src/…Ingestion` for this phase = 0
files) — the 5,000 msg/sec ingest benchmark does not apply this phase, the same
disposition as Phase 8 (alerts) and Phase 9 (dashboards).
