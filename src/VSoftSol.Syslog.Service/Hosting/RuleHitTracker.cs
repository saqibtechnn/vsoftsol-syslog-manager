using System.Collections.Concurrent;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// Accumulates rule-fire counts on the ingest path (cheap, in-memory) so
/// <see cref="ActionDispatchService"/> can flush them to <c>rules.hit_count</c> /
/// <c>last_fired_utc</c> in a single periodic write — never a write per message.
/// </summary>
public sealed class RuleHitTracker
{
    private readonly ConcurrentDictionary<long, Entry> _hits = new();

    public void Record(long ruleId, DateTimeOffset firedAt) =>
        _hits.AddOrUpdate(
            ruleId,
            _ => new Entry(1, firedAt),
            (_, existing) => new Entry(existing.Count + 1, firedAt > existing.LastFired ? firedAt : existing.LastFired));

    /// <summary>Atomically takes and clears the pending deltas.</summary>
    public IReadOnlyDictionary<long, (long Delta, DateTimeOffset LastFired)> Drain()
    {
        var result = new Dictionary<long, (long, DateTimeOffset)>();
        foreach (long key in _hits.Keys)
        {
            if (_hits.TryRemove(key, out Entry entry))
            {
                result[key] = (entry.Count, entry.LastFired);
            }
        }

        return result;
    }

    private readonly record struct Entry(long Count, DateTimeOffset LastFired);
}
