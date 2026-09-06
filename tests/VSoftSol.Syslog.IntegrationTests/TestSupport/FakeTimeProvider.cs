namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>
/// A hand-rolled controllable <see cref="TimeProvider"/> (TESTING_STANDARDS.md §2.2 — no
/// <c>Thread.Sleep</c>, time is injected). <see cref="Advance"/> moves both the wall clock
/// and the high-resolution timestamp forward; timers created from it fire on advance.
/// </summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private readonly object _gate = new();
    private DateTimeOffset _utcNow;
    private long _timestamp;
    private readonly List<(long DueAt, Action Callback, FakeTimer Timer)> _timers = [];

    public FakeTimeProvider(DateTimeOffset? start = null)
    {
        _utcNow = start ?? new DateTimeOffset(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
        _timestamp = 0;
    }

    public override long TimestampFrequency => 10_000_000; // 100ns ticks, like Stopwatch on most platforms

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _utcNow;
        }
    }

    public override long GetTimestamp()
    {
        lock (_gate)
        {
            return _timestamp;
        }
    }

    public void Advance(TimeSpan by)
    {
        List<Action> due = [];
        lock (_gate)
        {
            _utcNow = _utcNow.Add(by);
            _timestamp += (long)(by.TotalSeconds * TimestampFrequency);
            foreach ((long dueAt, Action cb, FakeTimer timer) in _timers.ToList())
            {
                if (dueAt <= _timestamp && !timer.Disposed)
                {
                    due.Add(cb);
                    _timers.Remove((dueAt, cb, timer));
                }
            }
        }

        foreach (Action cb in due)
        {
            cb();
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var timer = new FakeTimer();
        lock (_gate)
        {
            if (dueTime != Timeout.InfiniteTimeSpan)
            {
                _timers.Add((_timestamp + (long)(dueTime.TotalSeconds * TimestampFrequency), () => callback(state), timer));
            }
        }

        return timer;
    }

    private sealed class FakeTimer : ITimer
    {
        public bool Disposed { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period) => true;

        public void Dispose() => Disposed = true;

        public ValueTask DisposeAsync()
        {
            Disposed = true;
            return ValueTask.CompletedTask;
        }
    }
}
