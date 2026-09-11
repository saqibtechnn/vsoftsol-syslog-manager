using System.Text;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>A minimal, deterministic event factory shared by the Phase 10 retention/report
/// tests (TESTING_STANDARDS.md — fixtures are data, not ad hoc per-test constructions).</summary>
public static class RetentionFixture
{
    public static SyslogEvent Event(DateTimeOffset receivedUtc, string message, IReadOnlyList<long>? streamIds = null) => new()
    {
        ReceivedUtc = receivedUtc,
        SourceIp = "10.0.0.1",
        Hostname = "host-1",
        Facility = Facility.Local0,
        Severity = Severity.Informational,
        Protocol = Protocol.Udp,
        Message = message,
        RawMessage = Encoding.UTF8.GetBytes(message),
        ParseStatus = ParseStatus.Rfc5424,
        StreamIds = streamIds ?? [],
    };
}
