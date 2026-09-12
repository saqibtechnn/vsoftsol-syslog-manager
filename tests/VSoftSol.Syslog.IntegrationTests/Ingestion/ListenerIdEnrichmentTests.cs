using FluentAssertions;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Listeners;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Service.Hosting;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Ingestion;

/// <summary>
/// v1.1 — P2-1: mirrors what <c>SyslogPlatformExtensions.AddCollectorRuntime</c>'s
/// <see cref="EventEnricher"/> does with <see cref="ListenerIdRegistry"/> — a real,
/// populated registry stamps <see cref="SyslogEvent.ListenerId"/> on every ingested event;
/// an unpopulated one (this process is not the one hosting the collector runtime, or the
/// event's protocol was never registered) leaves it at its unresolved default, never a
/// reason to drop or delay a message (same convention as <see cref="IngestRoutingEndToEndTests"/>).
/// </summary>
[Trait("Category", "Ingestion")]
public sealed class ListenerIdEnrichmentTests
{
    private static EventEnricher BuildEnricher(ListenerIdRegistry registry) =>
        (parsed, _) => new ValueTask<SyslogEvent>(
            registry.TryGetId(parsed.Protocol, out long id) ? parsed.WithListenerId(id) : parsed);

    [Fact]
    public async Task IngestedMessage_IsStampedWithTheRegisteredListenerId()
    {
        // A synthetic id the database has never heard of would trip the real
        // `events.listener_id REFERENCES listeners(listener_id)` FK (migration 001) and
        // hang the pipeline in its zero-message-loss retry loop — the registry must only
        // ever hold ids SqliteListenerStore actually issued, exactly as production does, so
        // the listener row is upserted against the harness's own database (the enricher
        // factory is handed that same SqliteTestDatabase) before any frame is accepted.
        var registry = new ListenerIdRegistry();

        await using IngestionHarness h = await IngestionHarness.CreateAsync(enricherFactory: db =>
        {
            var store = new SqliteListenerStore(db.Factory);
            long realListenerId = store.UpsertAsync(Protocol.Udp, "0.0.0.0", 514, enabled: true, CancellationToken.None)
                .GetAwaiter().GetResult();
            registry.SetId(Protocol.Udp, realListenerId);
            return BuildEnricher(registry);
        });
        h.StartPipeline();

        await h.Intake.AcceptAsync(new RawFrame(
            DateTimeOffset.UtcNow, "203.0.113.5", "udp:test", Protocol.Udp,
            System.Text.Encoding.UTF8.GetBytes("<13>hello"), truncated: false), default);
        await h.DrainAsync();

        registry.TryGetId(Protocol.Udp, out long expectedId);
        SyslogEvent e = await h.Db.Repository.QueryAsync(new LogQuery { Limit = 1 }, default).FirstAsync();
        e.ListenerId.Should().Be(expectedId);
    }

    [Fact]
    public async Task IngestedMessage_UnregisteredProtocol_LeavesListenerIdUnresolved_AndStillCommits()
    {
        var registry = new ListenerIdRegistry(); // nothing registered

        await using IngestionHarness h = await IngestionHarness.CreateAsync(enricherFactory: _ => BuildEnricher(registry));
        h.StartPipeline();

        await h.Intake.AcceptAsync(new RawFrame(
            DateTimeOffset.UtcNow, "203.0.113.6", "udp:test", Protocol.Udp,
            System.Text.Encoding.UTF8.GetBytes("<13>hello"), truncated: false), default);
        await h.DrainAsync();

        SyslogEvent e = await h.Db.Repository.QueryAsync(new LogQuery { Limit = 1 }, default).FirstAsync();
        e.ListenerId.Should().Be(0, "an unregistered protocol must never block or lose the message");
    }
}
