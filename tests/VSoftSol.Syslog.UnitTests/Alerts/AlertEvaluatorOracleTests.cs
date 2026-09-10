using FluentAssertions;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Rules.Alerts;
using Xunit;
using Xunit.Abstractions;
using static VSoftSol.Syslog.UnitTests.Alerts.AlertTestBuilders;

namespace VSoftSol.Syslog.UnitTests.Alerts;

/// <summary>
/// PHASE_08 differential oracle: an independent naive implementation of the firing decision
/// must agree with <see cref="AlertEvaluator"/> for thousands of generated
/// (alert, window-data) pairs. Compensating evidence for the blocked Stryker run (P3-3),
/// the same pattern as the Phase 6 routing oracle and the Phase 7 rules oracle.
/// </summary>
public sealed class AlertEvaluatorOracleTests(ITestOutputHelper output)
{
    private const int Cases = 5000;

    [Fact]
    public void ProductionEvaluator_AgreesWithTheNaiveDecision_ForEveryGeneratedCase()
    {
        var rng = new Random(20260909);
        var compiler = new AlertCompiler();
        DateTimeOffset now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        int divergences = 0;

        for (int i = 0; i < Cases; i++)
        {
            (CompiledAlert alert, AlertWindowData data) = Generate(rng, compiler);

            var actual = AlertEvaluator.Evaluate(alert, data, now).Breaches
                .Select(b => (b.GroupValue, b.ObservedValue))
                .OrderBy(x => x.GroupValue ?? "\0").ThenBy(x => x.ObservedValue)
                .ToList();

            var expected = Naive(alert, data, now)
                .OrderBy(x => x.GroupValue ?? "\0").ThenBy(x => x.ObservedValue)
                .ToList();

            if (!actual.SequenceEqual(expected))
            {
                divergences++;
                if (divergences <= 5)
                {
                    output.WriteLine($"case {i} ({alert.Type}): actual [{string.Join(",", actual)}] expected [{string.Join(",", expected)}]");
                }
            }
        }

        output.WriteLine($"{Cases} comparisons, {divergences} divergences");
        divergences.Should().Be(0);
    }

    private static IEnumerable<(string? GroupValue, long ObservedValue)> Naive(
        CompiledAlert alert, AlertWindowData data, DateTimeOffset now)
    {
        switch (alert.Type)
        {
            case AlertEvaluationType.Threshold:
                foreach (GroupCount gc in data.GroupCounts)
                {
                    if (gc.Count > alert.Threshold)
                    {
                        yield return (gc.GroupValue, gc.Count);
                    }
                }

                break;

            case AlertEvaluationType.DistinctCount:
                if (data.DistinctValueCount > alert.Threshold)
                {
                    yield return (null, data.DistinctValueCount);
                }

                break;

            case AlertEvaluationType.Absence:
                long matched = data.GroupCounts.Count == 0 ? 0 : data.GroupCounts[0].Count;
                if (matched == 0)
                {
                    yield return (null, 0);
                }

                break;

            case AlertEvaluationType.DeviceSilent:
                foreach (DeviceSilence ds in data.DeviceSilences)
                {
                    bool silent = ds.LastSeenUtc is null
                        || (now - ds.LastSeenUtc.Value).TotalMinutes > ds.ThresholdMinutes;
                    if (silent)
                    {
                        long observed = ds.LastSeenUtc is null
                            ? long.MaxValue
                            : (long)(now - ds.LastSeenUtc.Value).TotalMinutes;
                        yield return (ds.DeviceName, observed);
                    }
                }

                break;
        }
    }

    private static (CompiledAlert, AlertWindowData) Generate(Random rng, AlertCompiler compiler)
    {
        int kind = rng.Next(4);
        switch (kind)
        {
            case 0:
                {
                    int threshold = rng.Next(1, 20);
                    bool grouped = rng.Next(2) == 0;
                    CompiledAlert a = Compile(compiler, Threshold(threshold: threshold, groupBy: grouped ? "hostname" : null));
                    int groups = grouped ? rng.Next(0, 6) : 1;
                    var counts = new List<GroupCount>();
                    for (int g = 0; g < groups; g++)
                    {
                        counts.Add(new GroupCount(grouped ? $"h{g}" : null, rng.Next(0, 30), [rng.Next(1, 100)]));
                    }

                    return (a, new AlertWindowData { GroupCounts = counts });
                }

            case 1:
                {
                    int threshold = rng.Next(1, 20);
                    CompiledAlert a = Compile(compiler, DistinctCount("source_ip", threshold));
                    return (a, new AlertWindowData { DistinctValueCount = rng.Next(0, 30) });
                }

            case 2:
                {
                    CompiledAlert a = Compile(compiler, Absence(All(Cmp("app", Core.Conditions.ConditionOperator.Equals, "backup"))));
                    long matched = rng.Next(3) == 0 ? 0 : rng.Next(1, 5);
                    var counts = matched == 0 ? new List<GroupCount>() : [new GroupCount(null, matched, [])];
                    return (a, new AlertWindowData { GroupCounts = counts });
                }

            default:
                {
                    CompiledAlert a = Compile(compiler, DeviceSilent());
                    var silences = new List<DeviceSilence>();
                    int n = rng.Next(0, 5);
                    for (int d = 0; d < n; d++)
                    {
                        DateTimeOffset? seen = rng.Next(4) == 0
                            ? null
                            : new DateTimeOffset(2026, 9, 9, 12, 0, 0, TimeSpan.Zero).AddMinutes(-rng.Next(0, 120));
                        silences.Add(new DeviceSilence(d, $"dev{d}", seen, rng.Next(5, 60)));
                    }

                    return (a, new AlertWindowData { DeviceSilences = silences });
                }
        }
    }

    private static CompiledAlert Compile(AlertCompiler compiler, AlertDefinition def)
    {
        AlertCompileResult r = compiler.Compile(def);
        r.Success.Should().BeTrue(string.Join("; ", r.Errors));
        return r.Alert!;
    }
}
