using System.Text;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Reports;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Retention;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Retention;

/// <summary>PHASE_10 security validation: archive path traversal, malicious-archive-import
/// refusal (decompression bomb + hash mismatch, both before any extraction), and report/
/// restore scope enforcement (SECURITY_STANDARDS.md).</summary>
public sealed class RetentionSecurityTests : IAsyncLifetime
{
    private RetentionTestHarness _h = null!;

    public async Task InitializeAsync() => _h = await RetentionTestHarness.CreateAsync(seed: false);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    private static SyslogEvent Event(DateTimeOffset receivedUtc, string message, IReadOnlyList<long>? streamIds = null) => new()
    {
        ReceivedUtc = receivedUtc,
        SourceIp = "10.0.0.1",
        Facility = Facility.Local0,
        Severity = Severity.Informational,
        Protocol = Protocol.Udp,
        Message = message,
        RawMessage = Encoding.UTF8.GetBytes(message),
        ParseStatus = ParseStatus.Rfc5424,
        StreamIds = streamIds ?? [],
    };

    [Fact]
    public async Task ExportColdBatchAsync_WithAPathTraversalStreamName_NeverEscapesTheArchiveRoot()
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 1, DefaultWarmDays = 1 }, "test", CancellationToken.None);
        long evil = await _h.CreateStreamAsync("../../../../windows/system32/evil");
        await _h.SeedEventsAsync([Event(now.AddDays(-10), "m", [evil])]);

        await _h.Engine.TierToWarmBatchAsync(CancellationToken.None);
        await _h.Engine.ExportColdBatchAsync(CancellationToken.None);

        ArchiveRecord archive = (await _h.Archives.ListAsync(null, CancellationToken.None)).Single();
        string expectedRoot = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(_h.Db.DatabasePath)!, "archives"));
        string actualFull = Path.GetFullPath(archive.FilePath);

        // The real security boundary: the resolved path stays under the configured root no
        // matter what the stream name contained. A ".." substring can still appear *inside*
        // a single sanitised path segment (SanitizeSegment only strips *leading* dots, by
        // design — see ArchiveNamingTests) but that is harmless once there are no path
        // separators left to traverse with; containment is what's proven here.
        actualFull.Should().StartWith(expectedRoot, "the sanitised filename must stay under the archive root regardless of the stream name");
    }

    [Fact]
    public void ArchiveFile_Parse_RefusesADecompressionBomb_BeforeAllocatingItsFullSize()
    {
        // A highly compressible 50 MB plaintext compresses to a tiny file — exactly the
        // shape of a decompression-bomb archive. The cap must refuse it without ever
        // materialising the full 50 MB.
        byte[] bomb = new byte[50 * 1024 * 1024];
        Array.Fill(bomb, (byte)'A');
        var header = new ArchiveFile.ArchiveHeader
        {
            Product = "test",
            Version = "1.0",
            StreamName = "s",
            PeriodStartUtc = DateTimeOffset.UtcNow,
            PeriodEndUtc = DateTimeOffset.UtcNow,
            EventCount = 0,
            ExportedUtc = DateTimeOffset.UtcNow,
        };
        var row = new ArchiveFile.ArchiveEventRow
        {
            EventId = 1,
            ReceivedUtc = "2026-01-01T00:00:00Z",
            SourceIp = "1.1.1.1",
            Protocol = "udp",
            Message = Encoding.UTF8.GetString(bomb),
            RawMessageBase64 = Convert.ToBase64String(bomb),
            ParseStatus = "raw",
        };
        byte[] compressed = ArchiveFile.Build(header, [row], _h.Compressor, level: 19);
        compressed.Length.Should().BeLessThan(1024 * 1024, "the fixture must actually compress well or the test proves nothing");

        Action act = () => ArchiveFile.Parse(compressed, _h.Compressor, maxDecompressedBytes: 1024 * 1024);

        act.Should().Throw<InvalidDataException>();
    }

    [Fact]
    public async Task RestoreArchiveAsync_ChecksTheHash_BeforeCallingArchiveFileParse()
    {
        // Corrupt the file so it is not even valid JSON after decompression — if the engine
        // ever reached Parse before the hash check, this would throw a *different* exception
        // (a JSON/format error) instead of the hash-mismatch InvalidDataException.
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 1, DefaultWarmDays = 1 }, "test", CancellationToken.None);
        await _h.SeedEventsAsync([Event(_h.Clock.GetUtcNow().AddDays(-10), "m")]);
        await _h.Engine.TierToWarmBatchAsync(CancellationToken.None);
        await _h.Engine.ExportColdBatchAsync(CancellationToken.None);
        ArchiveRecord archive = (await _h.Archives.ListAsync(null, CancellationToken.None)).Single();

        await File.WriteAllBytesAsync(archive.FilePath, "this is not even a valid compressed archive"u8.ToArray());

        Func<Task> act = () => _h.Engine.RestoreArchiveAsync(archive.ArchiveId, "auditor1", TimeSpan.FromDays(1), CancellationToken.None);

        (await act.Should().ThrowAsync<InvalidDataException>()).WithMessage("*hash*");
    }

    [Fact]
    public async Task Report_ArchivedPeriodsOmitted_IsScopedToVisibleStreams_NotAllArchives()
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 1, DefaultWarmDays = 1 }, "test", CancellationToken.None);
        long visible = await _h.CreateStreamAsync("Visible");
        long hidden = await _h.CreateStreamAsync("Hidden");
        await _h.SeedEventsAsync([
            Event(now.AddDays(-10), "in visible stream", [visible]),
            Event(now.AddDays(-10), "in hidden stream", [hidden]),
        ]);
        await _h.Engine.TierToWarmBatchAsync(CancellationToken.None);
        await _h.Engine.ExportColdBatchAsync(CancellationToken.None);

        var restrictedScope = UserScope.Create([visible], null);
        var report = new ReportDefinition { Name = "r", TemplateKey = CannedReportCatalog.TopTalkers, TimeRangeDays = 30 };

        ReportContent content = await _h.Content.ResolveAsync(report, restrictedScope, "auditor1", now, CancellationToken.None);

        content.ArchivedPeriodsOmitted.Should().HaveCount(1, "only the visible stream's archive should be disclosed");
    }

    [Fact]
    public async Task RestoredEvents_StayScoped_AnOutOfScopeViewerCannotSeeThemEitherViaSearchOrReports()
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();
        await _h.Policies.SaveSettingsAsync(new RetentionSettings { DefaultHotDays = 1, DefaultWarmDays = 1 }, "test", CancellationToken.None);
        long streamA = await _h.CreateStreamAsync("A");
        long streamB = await _h.CreateStreamAsync("B");
        IReadOnlyList<long> ids = await _h.SeedEventsAsync([Event(now.AddDays(-10), "secret-in-b", [streamB])]);
        await _h.Engine.TierToWarmBatchAsync(CancellationToken.None);
        await _h.Engine.ExportColdBatchAsync(CancellationToken.None);
        ArchiveRecord archive = (await _h.Archives.ListAsync(null, CancellationToken.None)).Single();

        await _h.Engine.RestoreArchiveAsync(archive.ArchiveId, "administrator", TimeSpan.FromDays(1), CancellationToken.None);

        var scopeOnA = UserScope.Create([streamA], null);
        SyslogEvent? viaGetById = await _h.Scoped.GetByIdAsync(scopeOnA, ids[0], CancellationToken.None);
        viaGetById.Should().BeNull("the restored event belongs to stream B, out of this viewer's scope");

        var search = await _h.Scoped.SearchAsync(scopeOnA,
            new VSoftSol.Syslog.Data.Search.SearchRequest { QueryText = "secret-in-b", FromUtc = now.AddDays(-20), ToUtc = now },
            CancellationToken.None);
        search.Rows.Should().BeEmpty();

        var scopeOnB = UserScope.Create([streamB], null);
        SyslogEvent? viaGetByIdInScope = await _h.Scoped.GetByIdAsync(scopeOnB, ids[0], CancellationToken.None);
        viaGetByIdInScope.Should().NotBeNull("the restored event must remain visible to a viewer who IS in scope");
    }
}
