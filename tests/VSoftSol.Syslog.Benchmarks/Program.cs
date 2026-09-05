using BenchmarkDotNet.Running;
using VSoftSol.Syslog.Benchmarks;

// Entry point for the benchmark suite. Phase 0 ships one trivial benchmark so the
// harness is proven; the ingest-throughput benchmarks arrive in Phase 2.
BenchmarkSwitcher.FromAssembly(typeof(PriorityBenchmark).Assembly).Run(args);
