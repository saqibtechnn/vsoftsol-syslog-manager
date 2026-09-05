using System.Text;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>Builders for test events.</summary>
public static class SampleEvents
{
    public static SyslogEvent Minimal(
        string message = "test message",
        string sourceIp = "192.0.2.10",
        DateTimeOffset? receivedUtc = null) => new()
        {
            ReceivedUtc = receivedUtc ?? DateTimeOffset.UtcNow,
            SourceIp = sourceIp,
            Facility = Facility.Local0,
            Severity = Severity.Informational,
            Protocol = Protocol.Udp,
            Message = message,
            RawMessage = Encoding.UTF8.GetBytes(message),
            ParseStatus = ParseStatus.Raw,
        };

    public static SyslogEvent Full() => new()
    {
        ReceivedUtc = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero),
        EventUtc = new DateTimeOffset(2026, 3, 1, 11, 59, 58, TimeSpan.Zero),
        SourceIp = "198.51.100.7",
        Hostname = "core-sw-1",
        AppName = "%LINK",
        ProcId = "1234",
        MsgId = "UPDOWN",
        Facility = Facility.Local7,
        Severity = Severity.Warning,
        Protocol = Protocol.Tcp,
        ListenerId = 0,
        Message = "Interface GigabitEthernet0/1, changed state to down",
        RawMessage = Encoding.UTF8.GetBytes("<187>Mar  1 11:59:58 core-sw-1 %LINK-3-UPDOWN: Interface Gi0/1 down"),
        ParseStatus = ParseStatus.Rfc3164,
        OccurrenceCount = 3,
        StructuredDataJson = "{\"origin\":{\"ip\":\"198.51.100.7\"}}",
        Vendor = "cisco-ios",
        Fields =
        [
            new EventField("interface", "GigabitEthernet0/1"),
            new EventField("mnemonic", "UPDOWN"),
            new EventField("new_state", "down"),
        ],
    };
}
