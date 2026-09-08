using FluentAssertions;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging.Abstractions;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Devices;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Devices;
using VSoftSol.Syslog.Data.Streams;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Streams;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Devices;

/// <summary>
/// The full ingest enrichment path (PHASE_06 items 3 &amp; 6): frame → parse → resolve
/// device (auto-discovering) → route to streams → commit, all before the batch lands.
/// </summary>
[Trait("Category", "Ingestion")]
public sealed class IngestRoutingEndToEndTests
{
    private static VSoftSol.Syslog.Ingestion.EventEnricher BuildEnricher(SqliteTestDatabase db)
    {
        var deviceStore = new SqliteDeviceStore(db.Factory);
        var settingsStore = new SqliteDiscoverySettingsStore(db.Factory);
        var streamStore = new SqliteStreamStore(db.Factory);
        var resolver = new DeviceResolver(deviceStore, settingsStore, NullLogger<DeviceResolver>.Instance);

        return async (parsed, ct) =>
        {
            long? deviceId = await resolver.ResolveAsync(parsed.SourceIp, parsed.Hostname, ct);
            IReadOnlyList<StreamRow> rows = await streamStore.ListActiveAsync(ct);
            StreamRouter router = StreamRouter.Build(rows.Select(r =>
                new StreamDefinition(r.StreamId, r.Name, r.Enabled, r.IsCatchAll, r.Match)));
            return parsed.WithRouting(deviceId, router.Route(parsed));
        };
    }

    [Fact]
    public async Task IngestedMessage_IsAttributedToADiscoveredDevice_AndRoutedToItsStreams()
    {
        await using IngestionHarness h = await IngestionHarness.CreateAsync(enricherFactory: BuildEnricher);
        h.StartPipeline();

        await h.Intake.AcceptAsync(new VSoftSol.Syslog.Ingestion.RawFrame(
            DateTimeOffset.UtcNow, "203.0.113.77", "udp:test", Protocol.Udp,
            System.Text.Encoding.UTF8.GetBytes(
                "<38>Mar  1 22:14:15 fw01 sshd[1]: authentication failure for user root"),
            truncated: false), default);
        await h.DrainAsync();

        SyslogEvent e = await h.Db.Repository.QueryAsync(new LogQuery { Limit = 1 }, default).FirstAsync();
        e.DeviceId.Should().NotBeNull("the unknown source was auto-discovered and linked");

        // The event is in "All Messages" and "Authentication Failures".
        var streams = new List<string>();
        await using (SqliteConnection c = await h.Db.Factory.OpenAsync(default))
        {
            await using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = """
                SELECT s.name FROM event_streams es JOIN streams s ON s.stream_id = es.stream_id
                WHERE es.event_id = $id;
                """;
            cmd.Parameters.AddWithValue("$id", e.EventId);
            await using SqliteDataReader reader = await cmd.ExecuteReaderAsync();
            while (await reader.ReadAsync())
            {
                streams.Add(reader.GetString(0));
            }
        }

        streams.Should().Contain(new[] { "All Messages", "Authentication Failures" });

        // Exactly one pending device was created for that source.
        await using (SqliteConnection c = await h.Db.Factory.OpenAsync(default))
        {
            await using SqliteCommand cmd = c.CreateCommand();
            cmd.CommandText = "SELECT COUNT(*) FROM devices WHERE approval_status = 'pending';";
            Convert.ToInt64(await cmd.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture)
                .Should().Be(1);
        }
    }

    [Fact]
    public async Task UnroutableEventStillCommits_ViaTheRepositorySafetyNet()
    {
        // Enricher that throws — the pipeline must swallow it and commit the event anyway.
        VSoftSol.Syslog.Ingestion.EventEnricher boom = (_, _) => throw new InvalidOperationException("router down");

        await using IngestionHarness h = await IngestionHarness.CreateAsync(enricherFactory: _ => boom);
        h.StartPipeline();

        await h.Intake.AcceptAsync(new VSoftSol.Syslog.Ingestion.RawFrame(
            DateTimeOffset.UtcNow, "203.0.113.9", "udp:test", Protocol.Udp,
            System.Text.Encoding.UTF8.GetBytes("totally unparseable @@@ "), truncated: false), default);
        await h.DrainAsync();

        (await h.CommittedCountAsync()).Should().Be(1, "an enrichment failure never loses a message");
    }
}
