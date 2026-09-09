using VSoftSol.Syslog.Data.Notifications;

namespace VSoftSol.Syslog.Web.Notifications;

/// <summary>
/// The UI-facing view of the notification store (PHASE_07 "RaiseNotification" + the Phase 4
/// notification-centre shell). Read-mostly: notifications are raised by the rules engine,
/// the operator marks them read / dismisses them here.
/// </summary>
public sealed class NotificationService
{
    private readonly SqliteNotificationStore _store;

    public NotificationService(SqliteNotificationStore store) => _store = store;

    public Task<IReadOnlyList<NotificationRow>> RecentAsync(int limit, CancellationToken ct) =>
        _store.ListActiveAsync(limit, ct);

    public Task<long> UnreadCountAsync(CancellationToken ct) => _store.CountUnreadAsync(ct);

    public Task MarkReadAsync(long id, CancellationToken ct) => _store.MarkReadAsync(id, ct);

    public Task MarkAllReadAsync(CancellationToken ct) => _store.MarkAllReadAsync(ct);

    public Task DismissAsync(long id, CancellationToken ct) => _store.DismissAsync(id, ct);
}
