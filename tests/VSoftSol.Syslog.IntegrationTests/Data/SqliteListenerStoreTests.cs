using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Listeners;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Data;

/// <summary>
/// v1.1 — P2-1 (`docs/evidence/phase-02/known-issues.md`): persists the up-to-5 protocol
/// listeners into the pre-existing, previously-unused `listeners` table (migration 001) so
/// <c>events.listener_id</c> can finally be a real FK instead of always NULL. Upserting by
/// <c>name</c> (which already encodes protocol+bind+port, matching each
/// <see cref="VSoftSol.Syslog.Ingestion.ISyslogListener"/>'s own naming convention exactly)
/// means a restart with unchanged config reuses the same id, while a changed port (a live
/// rebind, or an admin editing the bind config) naturally gets a new id — a genuinely
/// different listener identity, not an in-place rewrite of history.
/// </summary>
[Trait("Category", "Data")]
public sealed class SqliteListenerStoreTests
{
    [Fact]
    public async Task UpsertAsync_SameNameTwice_ReturnsTheSameId_AndUpdatesEnabled()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteListenerStore(db.Factory);

        long first = await store.UpsertAsync(Protocol.Udp, "127.0.0.1", 5514, enabled: true, CancellationToken.None);
        long second = await store.UpsertAsync(Protocol.Udp, "127.0.0.1", 5514, enabled: false, CancellationToken.None);

        second.Should().Be(first, "the same protocol/bind/port is the same listener identity across restarts");

        IReadOnlyList<ListenerRecord> all = await store.ListAsync(CancellationToken.None);
        ListenerRecord row = all.Should().ContainSingle(l => l.ListenerId == first).Subject;
        row.Enabled.Should().BeFalse("the second upsert's enabled state must win");
        row.Protocol.Should().Be(Protocol.Udp);
        row.BindAddress.Should().Be("127.0.0.1");
        row.Port.Should().Be(5514);
        row.Name.Should().Be("udp:127.0.0.1:5514", "must match UdpSyslogListener's own Name format exactly");
    }

    [Fact]
    public async Task UpsertAsync_DifferentPort_CreatesADistinctRow()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteListenerStore(db.Factory);

        long before = await store.UpsertAsync(Protocol.Tcp, "0.0.0.0", 514, enabled: true, CancellationToken.None);
        long after = await store.UpsertAsync(Protocol.Tcp, "0.0.0.0", 6514, enabled: true, CancellationToken.None);

        after.Should().NotBe(before, "a port change (e.g. a live rebind) is a genuinely different listener identity");

        IReadOnlyList<ListenerRecord> all = await store.ListAsync(CancellationToken.None);
        all.Should().Contain(l => l.ListenerId == before).And.Contain(l => l.ListenerId == after,
            "both the old and new identity stay on record — old events correctly keep pointing at the old one");
    }

    [Fact]
    public async Task ListAsync_MultipleProtocols_ReturnsAll()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteListenerStore(db.Factory);

        await store.UpsertAsync(Protocol.Udp, "0.0.0.0", 514, enabled: true, CancellationToken.None);
        await store.UpsertAsync(Protocol.Tcp, "0.0.0.0", 514, enabled: true, CancellationToken.None);
        await store.UpsertAsync(Protocol.Tls, "0.0.0.0", 6514, enabled: false, CancellationToken.None);

        IReadOnlyList<ListenerRecord> all = await store.ListAsync(CancellationToken.None);

        all.Should().HaveCount(3);
        all.Select(l => l.Protocol).Should().BeEquivalentTo([Protocol.Udp, Protocol.Tcp, Protocol.Tls]);
    }
}
