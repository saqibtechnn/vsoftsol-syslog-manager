using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Data;

/// <summary>
/// PHASE_01 fuzz: hostile strings into every repository method; no exception escapes,
/// nothing is corrupted, the raw bytes come back exactly.
/// </summary>
public sealed class RepositoryFuzzTests
{
    public static TheoryData<string> HostileStrings()
    {
        var data = new TheoryData<string>
        {
            "\0\0\0 embedded nulls \0",
            "‮‏ right-to-left ‬ marks",
            "emoji 🧨🔥 and CJK 日本語 and combining é",
            new string('A', 1_000_000),
            "line1\r\nline2\rline3\nline4",
            "control \a\b\t\v\f chars",
            "￾￿ non-characters",
            "%n %s %x format specifiers",
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(HostileStrings))]
    public async Task HostileString_RoundTripsWithoutCorruptionOrException(string hostile)
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();

        byte[] rawBytes = Encoding.UTF8.GetBytes(hostile);
        var evt = new SyslogEvent
        {
            ReceivedUtc = DateTimeOffset.UtcNow,
            SourceIp = "192.0.2.55",
            Hostname = hostile,
            Message = hostile,
            RawMessage = rawBytes,
            Facility = Facility.Local0,
            Severity = Severity.Informational,
            Protocol = Protocol.Udp,
            ParseStatus = ParseStatus.Raw,
            Fields = [new EventField("payload", hostile)],
        };

        long id = await db.Repository.AppendAsync(evt, CancellationToken.None);

        // NUL cannot be stored in a SQLite TEXT column faithfully; the repository replaces
        // it with U+FFFD for the text projection while the raw BLOB keeps the true bytes.
        string expectedText = hostile.Replace('\0', '�');

        SyslogEvent? loaded = await db.Repository.GetByIdAsync(id, CancellationToken.None);
        loaded!.RawMessage.ToArray().Should().Equal(rawBytes, "raw bytes are preserved verbatim (Constraint 4)");
        loaded.Message.Should().Be(expectedText);
        loaded.Fields.Should().ContainSingle().Which.Value.Should().Be(expectedText);

        // Query paths must also tolerate it.
        await db.SyncSearchAsync();
        await foreach (SyslogEvent _ in db.Repository.QueryAsync(
            new LogQuery { FullText = hostile[..Math.Min(hostile.Length, 200)] }, CancellationToken.None))
        {
        }

        await db.Repository.GetContextAsync(id, 5, 5, CancellationToken.None);
    }

    [Fact]
    public async Task RawBytesThatAreNotValidUtf8_AreStoredAndReturnedByteForByte()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        byte[] invalid = [0xC3, 0x28, 0xA0, 0xA1, 0xFF, 0xFE, 0x00, 0x80];

        long id = await db.Repository.AppendAsync(new SyslogEvent
        {
            ReceivedUtc = DateTimeOffset.UtcNow,
            SourceIp = "192.0.2.56",
            Message = string.Empty,
            RawMessage = invalid,
            Facility = Facility.Kernel,
            Severity = Severity.Emergency,
            Protocol = Protocol.Udp,
            ParseStatus = ParseStatus.Raw,
        }, CancellationToken.None);

        SyslogEvent? loaded = await db.Repository.GetByIdAsync(id, CancellationToken.None);
        loaded!.RawMessage.ToArray().Should().Equal(invalid);
    }
}
