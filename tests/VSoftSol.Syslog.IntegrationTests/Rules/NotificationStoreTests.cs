using FluentAssertions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Notifications;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Rules;

public sealed class NotificationStoreTests
{
    [Fact]
    public async Task Raise_List_CountUnread_MarkRead_Dismiss()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteNotificationStore(db.Factory);

        long a = await store.RaiseAsync(NotificationLevel.Warning, "Auth failure", "body", ruleId: null, CancellationToken.None);
        await store.RaiseAsync(NotificationLevel.Critical, "Disk full", "body", ruleId: null, CancellationToken.None);

        (await store.CountUnreadAsync(CancellationToken.None)).Should().Be(2);

        var active = await store.ListActiveAsync(50, CancellationToken.None);
        active.Should().HaveCount(2);
        active[0].Title.Should().Be("Disk full", "newest first");

        await store.MarkReadAsync(a, CancellationToken.None);
        (await store.CountUnreadAsync(CancellationToken.None)).Should().Be(1);

        await store.DismissAsync(a, CancellationToken.None);
        (await store.ListActiveAsync(50, CancellationToken.None)).Should().ContainSingle();
    }

    [Fact]
    public async Task Raise_StoresHostileTitleVerbatim_EncodingHappensAtRender()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteNotificationStore(db.Factory);

        const string payload = "<script>alert(1)</script>";
        await store.RaiseAsync(NotificationLevel.Info, payload, payload, null, CancellationToken.None);

        var rows = await store.ListActiveAsync(1, CancellationToken.None);
        rows[0].Title.Should().Be(payload, "stored verbatim — the Razor layer encodes it");
    }

    [Fact]
    public async Task RaiseAsync_RejectsABlankTitle()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteNotificationStore(db.Factory);

        Func<Task> act = () => store.RaiseAsync(NotificationLevel.Info, "  ", "b", null, CancellationToken.None);
        await act.Should().ThrowAsync<ArgumentException>();
    }
}
