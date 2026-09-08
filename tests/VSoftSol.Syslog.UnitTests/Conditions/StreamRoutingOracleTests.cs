using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Rules.Streams;
using Xunit;
using Xunit.Abstractions;

namespace VSoftSol.Syslog.UnitTests.Conditions;

/// <summary>
/// PHASE_06 routing oracle: an independent naive matcher computes the expected stream
/// membership for 10,000 generated messages against 50 generated stream definitions;
/// the production <see cref="StreamRouter"/> must agree exactly.
/// </summary>
public sealed class StreamRoutingOracleTests(ITestOutputHelper output)
{
    private const int Streams = 50;
    private const int Messages = 10_000;

    [Fact]
    public void ProductionRouter_AgreesWithTheNaiveMatcher_ForEveryGeneratedMessage()
    {
        var rng = new Random(20260908);
        List<StreamDefinition> defs = GenerateStreams(rng, Streams);
        StreamRouter router = StreamRouter.Build(defs);

        router.CompileErrors.Should().BeEmpty("the generator only emits valid rules");

        var rulesById = defs.Where(d => !d.IsCatchAll).ToDictionary(d => d.StreamId, d => d.Match);
        long catchAllId = defs.Single(d => d.IsCatchAll).StreamId;

        int divergences = 0;
        for (int i = 0; i < Messages; i++)
        {
            SyslogEvent e = GenerateEvent(rng);

            HashSet<long> actual = router.Route(e).ToHashSet();

            var expected = new HashSet<long> { catchAllId };
            foreach ((long id, ConditionNode? match) in rulesById)
            {
                if (NaiveConditionMatcher.Matches(match, e))
                {
                    expected.Add(id);
                }
            }

            if (!actual.SetEquals(expected))
            {
                divergences++;
                if (divergences <= 5)
                {
                    output.WriteLine($"message {i}: only-router [{string.Join(",", actual.Except(expected))}] " +
                        $"only-oracle [{string.Join(",", expected.Except(actual))}]  msg='{e.Message}' sev={e.Severity}");
                }
            }
        }

        divergences.Should().Be(0);
    }

    [Fact]
    public void NonMatchingMessage_LandsOnlyInTheCatchAll()
    {
        var defs = new List<StreamDefinition>
        {
            new(1, "All Messages", Enabled: true, IsCatchAll: true, Match: null),
            new(2, "Auth", true, false, Group(ConditionJoin.Or, Cmp("message", ConditionOperator.Contains, "failed password"))),
        };
        StreamRouter router = StreamRouter.Build(defs);

        SyslogEvent e = Event("interface GigabitEthernet0/1 is up");
        router.Route(e).Should().Equal(1L);
    }

    [Fact]
    public void MessageMatchingThreeStreams_IsInExactlyThoseThreePlusCatchAll()
    {
        var defs = new List<StreamDefinition>
        {
            new(1, "All", true, true, null),
            new(2, "Has failed", true, false, Group(ConditionJoin.Or, Cmp("message", ConditionOperator.Contains, "failed"))),
            new(3, "From sshd", true, false, Group(ConditionJoin.Or, Cmp("app", ConditionOperator.Equals, "sshd"))),
            new(4, "Warnings", true, false, Group(ConditionJoin.Or, Cmp("severity", ConditionOperator.Equals, "warning"))),
            new(5, "From cron", true, false, Group(ConditionJoin.Or, Cmp("app", ConditionOperator.Equals, "cron"))),
        };
        StreamRouter router = StreamRouter.Build(defs);

        SyslogEvent e = Event("failed password for root", app: "sshd", severity: Severity.Warning);
        router.Route(e).Should().BeEquivalentTo(new[] { 1L, 2L, 3L, 4L });
    }

    [Fact]
    public void DisabledStream_DoesNotRoute()
    {
        var defs = new List<StreamDefinition>
        {
            new(1, "All", true, true, null),
            new(2, "Off", Enabled: false, false, Group(ConditionJoin.Or, Cmp("message", ConditionOperator.Contains, "x"))),
        };
        StreamRouter.Build(defs).Route(Event("x")).Should().Equal(1L);
    }

    // ---------------------------------------------------------------- generators

    private static List<StreamDefinition> GenerateStreams(Random rng, int count)
    {
        var list = new List<StreamDefinition> { new(1, "All Messages", true, true, null) };
        string[] words = ["failed", "denied", "up", "down", "config", "error", "login", "root", "interface", "power"];
        string[] hosts = ["core-sw-1", "edge-fw-1", "app-01", "db-01"];
        string[] apps = ["sshd", "cron", "kernel", "named"];

        for (int i = 2; i <= count; i++)
        {
            var group = new ConditionGroup { Join = rng.Next(2) == 0 ? ConditionJoin.And : ConditionJoin.Or };
            int terms = rng.Next(1, 4);
            for (int t = 0; t < terms; t++)
            {
                group.Children.Add(rng.Next(8) switch
                {
                    0 => Cmp("message", ConditionOperator.Contains, words[rng.Next(words.Length)]),
                    1 => Cmp("message", ConditionOperator.Matches, words[rng.Next(words.Length)] + "(ed|ing)?"),
                    2 => Cmp("hostname", ConditionOperator.Equals, hosts[rng.Next(hosts.Length)]),
                    3 => Cmp("hostname", ConditionOperator.StartsWith, hosts[rng.Next(hosts.Length)][..3]),
                    4 => Cmp("app", ConditionOperator.InList, string.Join(", ", apps.Where(_ => rng.Next(2) == 0).DefaultIfEmpty(apps[0]))),
                    5 => Cmp("severity", rng.Next(2) == 0 ? ConditionOperator.Equals : ConditionOperator.GreaterThan, rng.Next(0, 8).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    6 => Cmp("facility", ConditionOperator.Equals, rng.Next(0, 24).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                    _ => Cmp("parse_status", ConditionOperator.Equals, new[] { "raw", "rfc3164", "rfc5424" }[rng.Next(3)]),
                });
            }

            list.Add(new StreamDefinition(i, "Stream " + i, true, false, group));
        }

        return list;
    }

    private static SyslogEvent GenerateEvent(Random rng)
    {
        string[] words = ["failed", "denied", "accepted", "up", "down", "config", "error", "login", "root", "interface", "power", "changed"];
        string[] hosts = ["core-sw-1", "edge-fw-1", "app-01", "db-01", "core-sw-2"];
        string[] apps = ["sshd", "cron", "kernel", "named", "systemd"];

        string message = string.Join(' ', Enumerable.Range(0, rng.Next(3, 8)).Select(_ => words[rng.Next(words.Length)]));
        return Event(
            message,
            host: rng.Next(10) < 9 ? hosts[rng.Next(hosts.Length)] : null,
            app: apps[rng.Next(apps.Length)],
            severity: (Severity)rng.Next(0, 8),
            facility: (Facility)rng.Next(0, 24),
            parseStatus: (ParseStatus)rng.Next(0, 3));
    }

    private static ConditionComparison Cmp(string field, ConditionOperator op, string value) =>
        new() { Field = field, Operator = op, Value = value };

    private static ConditionGroup Group(ConditionJoin join, params ConditionNode[] children) =>
        new() { Join = join, Children = [.. children] };

    private static SyslogEvent Event(
        string message, string? host = "h", string? app = "sshd",
        Severity severity = Severity.Notice, Facility facility = Facility.Local0,
        ParseStatus parseStatus = ParseStatus.Rfc3164) => new()
        {
            ReceivedUtc = DateTimeOffset.UnixEpoch,
            SourceIp = "10.0.0.1",
            Hostname = host,
            AppName = app,
            Severity = severity,
            Facility = facility,
            Protocol = Protocol.Udp,
            Message = message,
            RawMessage = Encoding.UTF8.GetBytes(message),
            ParseStatus = parseStatus,
        };
}
