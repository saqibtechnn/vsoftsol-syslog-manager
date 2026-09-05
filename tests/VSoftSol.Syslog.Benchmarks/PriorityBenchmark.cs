using BenchmarkDotNet.Attributes;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Benchmarks;

/// <summary>Trivial Phase 0 benchmark: proves BenchmarkDotNet is wired.</summary>
[MemoryDiagnoser]
public class PriorityBenchmark
{
    [Benchmark]
    public int EncodeDecodeRoundTrip()
    {
        var priority = new SyslogPriority(Facility.Local7, Severity.Warning);
        return SyslogPriority.FromValue(priority.Value).Value;
    }
}
