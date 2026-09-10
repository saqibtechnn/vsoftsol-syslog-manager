using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Alerts;

/// <summary>
/// PHASE_08 "Boundary matrix": exactly at threshold, one below, one above, at window edges,
/// across a DST transition, across midnight UTC, and with events arriving out of order.
/// Windows are expressed in UTC seconds, so a DST change is a non-event — this proves it.
/// </summary>
public sealed class AlertBoundaryMatrixTests
{
    private static SyslogEvent Event(DateTimeOffset received) => new()
    {
        ReceivedUtc = received,
        SourceIp = "203.0.113.9",
        Hostname = "core-sw-1",
        Facility = Facility.Local0,
        Severity = Severity.Warning,
        Protocol = Protocol.Udp,
        Message = "x",
        RawMessage = Encoding.UTF8.GetBytes("x"),
        ParseStatus = ParseStatus.Rfc3164,
    };

    private static AlertDefinition Alert(int threshold) => new()
    {
        Name = "t",
        Type = AlertEvaluationType.Threshold,
        WindowSeconds = 300,
        IntervalSeconds = 60,
        Threshold = threshold,
        AutoResolve = false,
        Actions = [],
        ReNotifySeconds = 0,
    };

    [Theory]
    [InlineData(4, 5, false)] // one below
    [InlineData(5, 5, false)] // exactly at
    [InlineData(6, 5, true)]  // one above
    public async Task ThresholdBoundary(int eventCount, int threshold, bool shouldFire)
    {
        DateTimeOffset t0 = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(t0);
        await h.Store.CreateAsync(Alert(threshold), "op", CancellationToken.None);

        await h.Db.Repository.AppendBatchAsync(
            Enumerable.Range(0, eventCount).Select(i => Event(t0.AddSeconds(-120 + i))).ToList(), CancellationToken.None);
        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));

        (await h.OpenInstancesAsync()).Should().HaveCount(shouldFire ? 1 : 0);
    }

    [Fact]
    public async Task WindowEdges_EventAtTheInclusiveStartCounts_AtTheExclusiveEndDoesNot()
    {
        DateTimeOffset now = new(2026, 9, 9, 12, 5, 0, TimeSpan.Zero);
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(now);
        await h.Store.CreateAsync(Alert(threshold: 1), "op", CancellationToken.None);

        DateTimeOffset windowStart = now.AddSeconds(-300);
        await h.Db.Repository.AppendBatchAsync(
            [
                Event(windowStart),                 // inclusive lower bound → counts
                Event(windowStart.AddSeconds(150)),  // clearly inside → counts
                Event(windowStart.AddSeconds(-1)),   // one second before → excluded
                Event(now),                          // exclusive upper bound → excluded
            ],
            CancellationToken.None);

        await h.TickAsync(); // evaluate at exactly `now`, window [now-300, now)

        IReadOnlyList<AlertInstance> open = await h.OpenInstancesAsync();
        open.Should().ContainSingle();
        open[0].ObservedValue.Should().Be(2, "only the two events inside [start, end) count");
    }

    [Fact]
    public async Task AcrossMidnightUtc_TheWindowStillSpansCorrectly()
    {
        DateTimeOffset now = new(2026, 9, 10, 0, 2, 0, TimeSpan.Zero); // just after midnight UTC
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(now);
        await h.Store.CreateAsync(Alert(threshold: 2), "op", CancellationToken.None);

        await h.Db.Repository.AppendBatchAsync(
            [Event(new DateTimeOffset(2026, 9, 9, 23, 59, 0, TimeSpan.Zero)),
             Event(new DateTimeOffset(2026, 9, 10, 0, 1, 0, TimeSpan.Zero)),
             Event(new DateTimeOffset(2026, 9, 10, 0, 1, 30, TimeSpan.Zero))],
            CancellationToken.None);

        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));
        (await h.OpenInstancesAsync()).Should().ContainSingle("all three events are within the 5-minute window that spans midnight");
    }

    [Fact]
    public async Task AcrossADstTransition_IsANonEvent_BecauseWindowsAreUtc()
    {
        // US DST spring-forward 2026: 2026-03-08 07:00 UTC (02:00 -> 03:00 local US Eastern).
        DateTimeOffset now = new(2026, 3, 8, 7, 3, 0, TimeSpan.Zero);
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(now);
        await h.Store.CreateAsync(Alert(threshold: 2), "op", CancellationToken.None);

        await h.Db.Repository.AppendBatchAsync(
            [Event(now.AddMinutes(-4)), Event(now.AddMinutes(-2)), Event(now.AddMinutes(-1))], CancellationToken.None);

        await h.AdvanceAndTickAsync(TimeSpan.FromSeconds(1));
        (await h.OpenInstancesAsync()).Should().ContainSingle("the window is UTC seconds — a wall-clock DST jump does not shift it");
    }

    [Fact]
    public async Task OutOfOrderArrival_CountsByReceivedTime_NotInsertionOrder()
    {
        DateTimeOffset now = new(2026, 9, 9, 12, 0, 0, TimeSpan.Zero);
        await using AlertEvaluationHarness h = await AlertEvaluationHarness.CreateAsync(now);
        await h.Store.CreateAsync(Alert(threshold: 1), "op", CancellationToken.None);

        // inserted newest-first; two are in the window, one is an hour old
        await h.Db.Repository.AppendBatchAsync(
            [Event(now.AddSeconds(-30)), Event(now.AddHours(-1)), Event(now.AddSeconds(-90))], CancellationToken.None);

        await h.TickAsync();
        IReadOnlyList<AlertInstance> open = await h.OpenInstancesAsync();
        open.Should().ContainSingle();
        open[0].ObservedValue.Should().Be(2, "the hour-old event is outside the window regardless of insertion order");
    }
}
