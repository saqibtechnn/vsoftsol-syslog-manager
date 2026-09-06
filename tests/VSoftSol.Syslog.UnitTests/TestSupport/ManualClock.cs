namespace VSoftSol.Syslog.UnitTests.TestSupport;

/// <summary>Minimal controllable <see cref="TimeProvider"/> for time-dependent unit tests.</summary>
public sealed class ManualClock : TimeProvider
{
    private long _timestamp;
    private DateTimeOffset _utcNow = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override long TimestampFrequency => 10_000_000;

    public override long GetTimestamp() => Interlocked.Read(ref _timestamp);

    public override DateTimeOffset GetUtcNow() => _utcNow;

    public void Advance(TimeSpan by)
    {
        Interlocked.Add(ref _timestamp, (long)(by.TotalSeconds * TimestampFrequency));
        _utcNow = _utcNow.Add(by);
    }
}
