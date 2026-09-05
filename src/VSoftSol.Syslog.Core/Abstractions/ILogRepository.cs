using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Core.Abstractions;

/// <summary>
/// Persistence seam for stored events. One of only two seams in the product
/// (CLAUDE.md "Two seams only"). The v1 implementation is SQLite-backed; this interface
/// exists so a future PostgreSQL implementation can be dropped in without touching the
/// ingestion, rules, or reporting code.
/// </summary>
/// <remarks>
/// Implementations must never lose a committed event and must always persist
/// <see cref="SyslogEvent.RawMessage"/> verbatim, including for unparsed messages.
/// All methods are asynchronous; callers pass a <see cref="CancellationToken"/>.
/// </remarks>
public interface ILogRepository
{
    /// <summary>
    /// Durably append a single event. Returns the assigned <c>event_id</c>. The event is
    /// considered committed once this task completes successfully.
    /// </summary>
    Task<long> AppendAsync(SyslogEvent syslogEvent, CancellationToken cancellationToken);

    /// <summary>
    /// Durably append a batch of events in one transaction. Returns the assigned ids in
    /// input order. Either all events commit or none do.
    /// </summary>
    Task<IReadOnlyList<long>> AppendBatchAsync(
        IReadOnlyCollection<SyslogEvent> events,
        CancellationToken cancellationToken);

    /// <summary>Fetch a single event by id, or null if it does not exist.</summary>
    Task<SyslogEvent?> GetByIdAsync(long eventId, CancellationToken cancellationToken);

    /// <summary>
    /// Stream events matching <paramref name="query"/>, honouring its ordering, limit,
    /// and offset. The caller is responsible for having already applied scope filtering
    /// to the query.
    /// </summary>
    IAsyncEnumerable<SyslogEvent> QueryAsync(LogQuery query, CancellationToken cancellationToken);

    /// <summary>Count events matching <paramref name="query"/> (ignoring limit and offset).</summary>
    Task<long> CountAsync(LogQuery query, CancellationToken cancellationToken);

    /// <summary>
    /// Return up to <paramref name="before"/> events immediately preceding and
    /// <paramref name="after"/> events immediately following <paramref name="eventId"/>
    /// from the same source host, ordered oldest-first. Backs the "context view".
    /// </summary>
    Task<IReadOnlyList<SyslogEvent>> GetContextAsync(
        long eventId,
        int before,
        int after,
        CancellationToken cancellationToken);
}
