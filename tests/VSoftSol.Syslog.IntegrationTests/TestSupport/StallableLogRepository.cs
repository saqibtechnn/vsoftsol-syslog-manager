using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>
/// Wraps a real <see cref="ILogRepository"/> and can hold every <see cref="AppendBatchAsync"/>
/// call at a gate (chaos / backpressure tests) or make it throw. Reads pass straight through.
/// </summary>
public sealed class StallableLogRepository(ILogRepository inner) : ILogRepository
{
    private volatile bool _stalled;
    private volatile int _throwTimes;
    private long _committed;

    public long CommittedRows => Interlocked.Read(ref _committed);

    public void Stall() => _stalled = true;

    public void Release() => _stalled = false;

    /// <summary>Make the next <paramref name="times"/> append attempts throw.</summary>
    public void FailNext(int times) => _throwTimes = times;

    public async Task<long> AppendAsync(SyslogEvent syslogEvent, CancellationToken cancellationToken)
    {
        IReadOnlyList<long> ids = await AppendBatchAsync([syslogEvent], cancellationToken).ConfigureAwait(false);
        return ids[0];
    }

    public async Task<IReadOnlyList<long>> AppendBatchAsync(
        IReadOnlyCollection<SyslogEvent> events, CancellationToken cancellationToken)
    {
        while (_stalled)
        {
            await Task.Delay(15, cancellationToken).ConfigureAwait(false);
        }

        if (Interlocked.Decrement(ref _throwTimes) >= 0)
        {
            throw new InvalidOperationException("Injected commit failure.");
        }

        IReadOnlyList<long> ids = await inner.AppendBatchAsync(events, cancellationToken).ConfigureAwait(false);
        Interlocked.Add(ref _committed, events.Count);
        return ids;
    }

    public Task<SyslogEvent?> GetByIdAsync(long eventId, CancellationToken cancellationToken) =>
        inner.GetByIdAsync(eventId, cancellationToken);

    public IAsyncEnumerable<SyslogEvent> QueryAsync(LogQuery query, CancellationToken cancellationToken) =>
        inner.QueryAsync(query, cancellationToken);

    public Task<long> CountAsync(LogQuery query, CancellationToken cancellationToken) =>
        inner.CountAsync(query, cancellationToken);

    public Task<IReadOnlyList<SyslogEvent>> GetContextAsync(long eventId, int before, int after, CancellationToken cancellationToken) =>
        inner.GetContextAsync(eventId, before, after, cancellationToken);
}
