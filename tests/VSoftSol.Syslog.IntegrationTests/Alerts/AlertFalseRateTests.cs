using System.Text;
using System.Text.Json;
using FluentAssertions;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;
using Xunit.Abstractions;

namespace VSoftSol.Syslog.IntegrationTests.Alerts;

/// <summary>
/// PHASE_08 "Labelled dataset for false positive/negative rate". Replays a version-controlled
/// set of hand-labelled scenarios (<c>tests/fixtures/alerts/labelled-scenarios.json</c>) and
/// measures FP (fired but should not have) and FN (should have fired but did not). The rates
/// are stated in <c>docs/evidence/phase-08/fp-fn-rates.md</c>.
/// </summary>
public sealed class AlertFalseRateTests(ITestOutputHelper output)
{
    private static readonly DateTimeOffset T0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);

    private sealed record Scenario(string Name, AlertSpec Alert, EventSpec[] Events, int EvaluateAtOffsetSeconds, bool ExpectFire);

    private sealed record AlertSpec(string Type, int WindowSeconds, int IntervalSeconds, int Threshold, string? GroupBy, string[] FilterContains);

    private sealed record EventSpec(int OffsetSeconds, string? Hostname, string? SourceIp, string Message);

    [Fact]
    public async Task LabelledScenarios_MeasuredFalsePositiveAndFalseNegativeRates_AreZero()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixtures", "alerts", "labelled-scenarios.json");
        File.Exists(path).Should().BeTrue($"the version-controlled fixture must ship: {path}");

        using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
        var options = new JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        Scenario[] scenarios = doc.RootElement.GetProperty("scenarios").EnumerateArray()
            .Select(e => e.Deserialize<Scenario>(options)!)
            .ToArray();

        scenarios.Should().HaveCountGreaterThanOrEqualTo(10);

        int falsePositives = 0;
        int falseNegatives = 0;
        int truePositives = 0;
        int trueNegatives = 0;

        foreach (Scenario s in scenarios)
        {
            bool fired = await RunScenarioAsync(s);
            if (s.ExpectFire && fired)
            {
                truePositives++;
            }
            else if (s.ExpectFire && !fired)
            {
                falseNegatives++;
                output.WriteLine($"FALSE NEGATIVE: {s.Name}");
            }
            else if (!s.ExpectFire && fired)
            {
                falsePositives++;
                output.WriteLine($"FALSE POSITIVE: {s.Name}");
            }
            else
            {
                trueNegatives++;
            }
        }

        int positives = truePositives + falseNegatives;
        int negatives = trueNegatives + falsePositives;
        output.WriteLine($"scenarios={scenarios.Length} TP={truePositives} TN={trueNegatives} FP={falsePositives} FN={falseNegatives}");
        output.WriteLine($"false-positive rate = {(negatives == 0 ? 0 : (double)falsePositives / negatives):P1}");
        output.WriteLine($"false-negative rate = {(positives == 0 ? 0 : (double)falseNegatives / positives):P1}");

        falsePositives.Should().Be(0);
        falseNegatives.Should().Be(0);
    }

    private static async Task<bool> RunScenarioAsync(Scenario s)
    {
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(T0);

        var alert = new AlertDefinition
        {
            Name = s.Name.Length > 90 ? s.Name[..90] : s.Name,
            Type = s.Alert.Type switch
            {
                "distinct_count" => AlertEvaluationType.DistinctCount,
                "absence" => AlertEvaluationType.Absence,
                "device_silent" => AlertEvaluationType.DeviceSilent,
                _ => AlertEvaluationType.Threshold,
            },
            WindowSeconds = s.Alert.WindowSeconds,
            IntervalSeconds = s.Alert.IntervalSeconds,
            Threshold = Math.Max(1, s.Alert.Threshold),
            GroupByField = s.Alert.GroupBy,
            Filter = s.Alert.FilterContains.Length == 0 ? null : new ConditionGroup
            {
                Join = ConditionJoin.Or,
                Children = [.. s.Alert.FilterContains.Select(t => (ConditionNode)new ConditionComparison
                {
                    Field = "message",
                    Operator = ConditionOperator.Contains,
                    Value = t,
                })],
            },
            AutoResolve = false,
            ReNotifySeconds = 0,
            Actions = [],
        };
        await h.Store.CreateAsync(alert, "fixture", CancellationToken.None);

        var events = s.Events.Select(e =>
        {
            string message = e.Message;
            return new SyslogEvent
            {
                ReceivedUtc = T0.AddSeconds(e.OffsetSeconds),
                SourceIp = e.SourceIp ?? "203.0.113.1",
                Hostname = e.Hostname,
                Facility = Facility.Local0,
                Severity = Severity.Warning,
                Protocol = Protocol.Udp,
                Message = message,
                RawMessage = Encoding.UTF8.GetBytes(message),
                ParseStatus = ParseStatus.Rfc3164,
            };
        }).ToList();
        if (events.Count > 0)
        {
            await h.Db.Repository.AppendBatchAsync(events, CancellationToken.None);
        }

        h.Clock.Advance(TimeSpan.FromSeconds(Math.Max(1, s.EvaluateAtOffsetSeconds + 1)));
        await h.TickAsync();

        return (await h.OpenInstancesAsync()).Count > 0;
    }
}
