using Microsoft.Extensions.Options;

namespace VSoftSol.Syslog.Ingestion.Parsing;

/// <summary>
/// Short-window de-noiser (PHASE_03 item 8): an identical message from the same host seen
/// again within <see cref="ParsingOptions.DeduplicationWindow"/> bumps the earlier event's
/// <c>occurrence_count</c> instead of inserting a new row. Disabled when the window is
/// <c>00:00:00</c> (the default). In-memory only — a restart resets it, which is fine:
/// dedup is a convenience, never a correctness guarantee.
/// </summary>
public sealed class DeduplicationWindow
{
    private readonly Dictionary<string, Entry> _seen = new(StringComparer.Ordinal);
    private readonly object _gate = new();
    private readonly TimeProvider _time;
    private readonly TimeSpan _window;
    private long _lastPrune;

    public DeduplicationWindow(IOptions<ParsingOptions> options, TimeProvider? timeProvider = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _window = options.Value.DeduplicationWindow;
    }

    public bool Enabled => _window > TimeSpan.Zero;

    /// <summary>
    /// If <paramref name="key"/> was recorded within the window, returns that event id and
    /// refreshes the entry (so a stream of duplicates keeps folding in). Otherwise null.
    /// </summary>
    public long? LookupRecent(string key)
    {
        if (!Enabled)
        {
            return null;
        }

        long now = _time.GetTimestamp();
        lock (_gate)
        {
            MaybePrune(now);
            if (_seen.TryGetValue(key, out Entry entry) && _time.GetElapsedTime(entry.Stamp, now) <= _window)
            {
                _seen[key] = entry with { Stamp = now };
                return entry.EventId;
            }

            return null;
        }
    }

    /// <summary>Records a freshly-committed event so later duplicates can fold into it.</summary>
    public void Record(string key, long eventId)
    {
        if (!Enabled)
        {
            return;
        }

        lock (_gate)
        {
            _seen[key] = new Entry(eventId, _time.GetTimestamp());
        }
    }

    private void MaybePrune(long now)
    {
        if (_time.GetElapsedTime(_lastPrune, now) < _window)
        {
            return;
        }

        _lastPrune = now;
        var stale = new List<string>();
        foreach ((string k, Entry e) in _seen)
        {
            if (_time.GetElapsedTime(e.Stamp, now) > _window)
            {
                stale.Add(k);
            }
        }

        foreach (string k in stale)
        {
            _seen.Remove(k);
        }
    }

    private readonly record struct Entry(long EventId, long Stamp);
}
