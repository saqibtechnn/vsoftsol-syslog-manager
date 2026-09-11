using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Retention;

/// <summary>PHASE_10 — archive restore: byte-identical recovery, searchability, auto-expiry,
/// and tamper refusal before any extraction (SECURITY_STANDARDS.md "Malicious archive import").</summary>
public sealed class RestoreTests : IAsyncLifetime
{
    private RetentionTestHarness _h = null!;

    public async Task InitializeAsync() => _h = await RetentionTestHarness.CreateAsync(seed: false);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private static SyslogEvent Event(DateTimeOffset receivedUtc, string message, byte[] raw, IReadOnlyList<EventField>? fields = null) => new()
    {
        ReceivedUtc = receivedUtc,
        SourceIp = "10.0.0.9",
        Hostname = "fw-1",
        AppName = "iptables",
        Facility = Facility.Local3,
        Severity = Severity.Warning,
        Protocol = Protocol.Udp,
        Message = message,
        RawMessage = raw,
        ParseStatus = ParseStatus.Rfc5424,
        Fields = fields ?? [],
    };

    private async Task<ArchiveRecord> ArchiveOneEventAsync(SyslogEvent evt)
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 1, DefaultWarmDays = 1 }, "test", CancellationToken.None);
        await _h.SeedEventsAsync([evt]);
        await _h.Engine.TierToWarmBatchAsync(CancellationToken.None);
        await _h.Engine.ExportColdBatchAsync(CancellationToken.None);
        _ = now;
        return (await _h.Archives.ListAsync(null, CancellationToken.None)).Single();
    }

    [Fact]
    public async Task RestoreArchiveAsync_ReinstatesByteIdenticalData()
    {
        byte[] raw = [0, 1, 2, 200, (byte)'\n'];
        var original = Event(_h.Clock.GetUtcNow().AddDays(-10), "café configuration changed", raw,
            [new EventField("bytes", "1234")]);
        ArchiveRecord archive = await ArchiveOneEventAsync(original);

        RestoreRecord restore = await _h.Engine.RestoreArchiveAsync(archive.ArchiveId, "auditor1", TimeSpan.FromDays(3), CancellationToken.None);

        restore.EventCount.Should().Be(1);
        restore.Status.Should().Be(RestoreStatus.Active);

        var search = await _h.Scoped.SearchAsync(UserScope.Unrestricted,
            new VSoftSol.Syslog.Data.Search.SearchRequest { QueryText = "", FromUtc = original.ReceivedUtc.AddDays(-1), ToUtc = original.ReceivedUtc.AddDays(1) },
            CancellationToken.None);

        search.Rows.Should().ContainSingle();
        SyslogEvent restored = search.Rows[0];
        restored.Message.Should().Be(original.Message);
        restored.RawMessage.ToArray().Should().Equal(raw);
        restored.Hostname.Should().Be(original.Hostname);
        restored.Fields.Should().ContainSingle(f => f.Name == "bytes" && f.Value == "1234");
    }

    [Fact]
    public async Task RestoreArchiveAsync_IsSearchableByFreeText()
    {
        var original = Event(_h.Clock.GetUtcNow().AddDays(-10), "unique-marker-xyz occurred", "raw"u8.ToArray());
        ArchiveRecord archive = await ArchiveOneEventAsync(original);
        await _h.Engine.RestoreArchiveAsync(archive.ArchiveId, "auditor1", TimeSpan.FromDays(1), CancellationToken.None);

        var search = await _h.Scoped.SearchAsync(UserScope.Unrestricted,
            new VSoftSol.Syslog.Data.Search.SearchRequest { QueryText = "unique-marker-xyz", FromUtc = original.ReceivedUtc.AddDays(-1), ToUtc = original.ReceivedUtc.AddDays(1) },
            CancellationToken.None);

        search.Rows.Should().ContainSingle();
    }

    [Fact]
    public async Task ExpireRestoresBatchAsync_RemovesTheRestoredEvents_AfterTheExpiryPasses()
    {
        var original = Event(_h.Clock.GetUtcNow().AddDays(-10), "temp restore", "raw"u8.ToArray());
        ArchiveRecord archive = await ArchiveOneEventAsync(original);
        await _h.Engine.RestoreArchiveAsync(archive.ArchiveId, "auditor1", TimeSpan.FromDays(1), CancellationToken.None);

        _h.Clock.Advance(TimeSpan.FromDays(2));
        int expired = await _h.Engine.ExpireRestoresBatchAsync(100, CancellationToken.None);

        expired.Should().Be(1);
        var search = await _h.Scoped.SearchAsync(UserScope.Unrestricted,
            new VSoftSol.Syslog.Data.Search.SearchRequest { QueryText = "", FromUtc = original.ReceivedUtc.AddDays(-1), ToUtc = original.ReceivedUtc.AddDays(1) },
            CancellationToken.None);
        search.Rows.Should().BeEmpty();

        IReadOnlyList<RestoreRecord> active = await _h.Restores.ListActiveAsync(CancellationToken.None);
        active.Should().BeEmpty();
    }

    [Fact]
    public async Task RestoreArchiveAsync_WithATamperedFile_RefusesBeforeInsertingAnything()
    {
        var original = Event(_h.Clock.GetUtcNow().AddDays(-10), "will be tampered", "raw"u8.ToArray());
        ArchiveRecord archive = await ArchiveOneEventAsync(original);

        byte[] bytes = await File.ReadAllBytesAsync(archive.FilePath);
        bytes[^1] ^= 0xFF; // flip the last byte
        await File.WriteAllBytesAsync(archive.FilePath, bytes);

        Func<Task> act = () => _h.Engine.RestoreArchiveAsync(archive.ArchiveId, "auditor1", TimeSpan.FromDays(1), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidDataException>();

        ArchiveRecord? after = await _h.Archives.GetAsync(archive.ArchiveId, CancellationToken.None);
        after!.Status.Should().Be(ArchiveStatus.TamperDetected);

        var search = await _h.Scoped.SearchAsync(UserScope.Unrestricted,
            new VSoftSol.Syslog.Data.Search.SearchRequest { QueryText = "", FromUtc = original.ReceivedUtc.AddDays(-1), ToUtc = original.ReceivedUtc.AddDays(1) },
            CancellationToken.None);
        search.Rows.Should().BeEmpty("a tampered archive must not restore any row, not even a partial set");
    }

    [Fact]
    public async Task RestoreArchiveAsync_OfADeletedArchive_Throws()
    {
        var original = Event(_h.Clock.GetUtcNow().AddDays(-10), "will be deleted", "raw"u8.ToArray());
        ArchiveRecord archive = await ArchiveOneEventAsync(original);
        await _h.Archives.MarkDeletedAsync(archive.ArchiveId, CancellationToken.None);

        Func<Task> act = () => _h.Engine.RestoreArchiveAsync(archive.ArchiveId, "auditor1", TimeSpan.FromDays(1), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }

    [Fact]
    public async Task RestoreArchiveAsync_OfANonExistentArchive_Throws()
    {
        Func<Task> act = () => _h.Engine.RestoreArchiveAsync(999_999, "auditor1", TimeSpan.FromDays(1), CancellationToken.None);
        await act.Should().ThrowAsync<InvalidOperationException>();
    }
}
