using System.Text;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>
/// A deterministic, version-controlled event set for the Phase 9 aggregation tests
/// (TESTING_STANDARDS §2.9 — fixtures are data, not generated from the code under test).
/// 360 events over a fixed 6-hour UTC window across three hosts / three apps / a spread of
/// severities, a third of them carrying a numeric <c>bytes</c> extracted field. The exact
/// per-group / per-bucket numbers are whatever they are — the oracle test recomputes them
/// with independent SQL and compares.
/// </summary>
public static class AggregationFixture
{
    public static readonly DateTimeOffset WindowStart = new(2026, 6, 1, 0, 0, 0, TimeSpan.Zero);

    public static readonly DateTimeOffset WindowEnd = WindowStart.AddHours(6);

    public const int EventCount = 360;

    private static readonly string[] Hosts = ["web-1", "web-2", "db-1"];
    private static readonly string[] Apps = ["sshd", "nginx", "postgres"];

    public static IReadOnlyList<SyslogEvent> Build()
    {
        var events = new List<SyslogEvent>(EventCount);
        for (int i = 0; i < EventCount; i++)
        {
            // Spread across the 6h window: event i at minute (i) — 360 minutes = 6 hours.
            DateTimeOffset received = WindowStart.AddMinutes(i);
            string host = Hosts[i % 3];
            string app = Apps[i % 3 == 0 ? (i / 3) % 3 : i % 3];
            var severity = (Severity)(i % 8);
            string message = $"event {i} on {host}";

            var fields = new List<EventField>();
            if (i % 3 == 0)
            {
                fields.Add(new EventField("bytes", ((i % 10 + 1) * 100).ToString(System.Globalization.CultureInfo.InvariantCulture)));
            }

            events.Add(new SyslogEvent
            {
                ReceivedUtc = received,
                EventUtc = received,
                SourceIp = $"10.1.{i % 4}.{i % 200 + 1}",
                Hostname = host,
                AppName = app,
                Facility = (Facility)(i % 24),
                Severity = severity,
                Protocol = Protocol.Udp,
                Message = message,
                RawMessage = Encoding.UTF8.GetBytes(message),
                ParseStatus = i % 9 == 0 ? ParseStatus.Raw : ParseStatus.Rfc5424,
                OccurrenceCount = i % 5 + 1,
                Fields = fields,
            });
        }

        return events;
    }
}
