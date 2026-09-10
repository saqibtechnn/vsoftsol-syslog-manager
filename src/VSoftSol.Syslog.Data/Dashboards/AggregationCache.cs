using System.Collections.Concurrent;
using System.Globalization;
using System.Text;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Security;

namespace VSoftSol.Syslog.Data.Dashboards;

/// <summary>
/// A short-TTL in-process cache for widget aggregation results (PHASE_09 build item 8 —
/// "a shared wall dashboard does not hammer the database"). The key is fingerprinted by the
/// viewer's <see cref="UserScope"/>, so one user's cached aggregate can <em>never</em> be
/// served to another (PHASE_09 security: cache poisoning / cross-scope leak). Entries are
/// computed at most once under concurrency (stampede protection) and expire by wall clock;
/// a time-range change changes the key, which is the invalidation.
/// </summary>
/// <remarks>
/// Deliberately hand-rolled rather than pulling in <c>Microsoft.Extensions.Caching.Memory</c>
/// — Phase 7/8 shipped no new dependency and this needs perhaps 40 lines. Single node, one
/// process (ADR 0005), so a <see cref="ConcurrentDictionary{TKey,TValue}"/> is sufficient.
/// </remarks>
public sealed class AggregationCache
{
    private const int MaxEntries = 512;

    private readonly ConcurrentDictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly Func<TimeSpan> _ttl;

    public AggregationCache(TimeProvider? timeProvider = null, Func<TimeSpan>? ttl = null)
    {
        _time = timeProvider ?? TimeProvider.System;
        _ttl = ttl ?? (() => TimeSpan.FromSeconds(15));
    }

    /// <summary>Live entry count (test / diagnostics only).</summary>
    public int Count => _entries.Count;

    /// <summary>Drops every entry (used when a dashboard's widgets change).</summary>
    public void Clear() => _entries.Clear();

    private sealed record Entry(Lazy<Task<object>> Value, DateTimeOffset ExpiresUtc);

    /// <summary>
    /// Returns the cached result for <paramref name="keyParts"/> under
    /// <paramref name="scope"/>, or runs <paramref name="factory"/> once and caches it for
    /// the TTL.
    /// </summary>
    public async Task<T> GetOrAddAsync<T>(UserScope scope, string keyParts, Func<CancellationToken, Task<T>> factory, CancellationToken cancellationToken)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(factory);

        string key = ScopeFingerprint(scope) + "␟" + keyParts;
        DateTimeOffset now = _time.GetUtcNow();

        while (true)
        {
            Entry entry = _entries.GetOrAdd(key, _ => new Entry(
                new Lazy<Task<object>>(async () => (await factory(cancellationToken).ConfigureAwait(false))!),
                now + _ttl()));

            if (entry.ExpiresUtc <= now)
            {
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                continue;
            }

            try
            {
                object value = await entry.Value.Value.ConfigureAwait(false);
                Trim(now);
                return (T)value;
            }
            catch
            {
                // A failed factory must not stick — drop the entry so the next caller retries.
                _entries.TryRemove(new KeyValuePair<string, Entry>(key, entry));
                throw;
            }
        }
    }

    private void Trim(DateTimeOffset now)
    {
        if (_entries.Count <= MaxEntries)
        {
            return;
        }

        foreach (KeyValuePair<string, Entry> kv in _entries)
        {
            if (kv.Value.ExpiresUtc <= now)
            {
                _entries.TryRemove(kv);
            }
        }

        // Still over budget → drop the entries closest to expiry.
        if (_entries.Count > MaxEntries)
        {
            foreach (KeyValuePair<string, Entry> kv in _entries.OrderBy(e => e.Value.ExpiresUtc).Take(_entries.Count - MaxEntries))
            {
                _entries.TryRemove(kv);
            }
        }
    }

    /// <summary>
    /// A stable, collision-resistant string identity for a scope. Unrestricted is its own
    /// value; a restricted scope folds its (sorted) stream and device-group id sets and the
    /// "all streams" / "all groups" flags. Two equal scopes fingerprint identically; two
    /// different scopes never collide.
    /// </summary>
    public static string ScopeFingerprint(UserScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);
        if (scope.IsUnrestricted)
        {
            return "unrestricted";
        }

        var sb = new StringBuilder(64);
        sb.Append("streams:").Append(scope.AllStreams ? "*" : Join(scope.StreamIds));
        sb.Append(";groups:").Append(scope.AllDeviceGroups ? "*" : Join(scope.DeviceGroupIds));
        return sb.ToString();
    }

    private static string Join(IReadOnlyCollection<long> ids)
    {
        if (ids.Count == 0)
        {
            return "none";
        }

        var sorted = ids.ToArray();
        Array.Sort(sorted);
        return string.Join(',', sorted.Select(i => i.ToString(CultureInfo.InvariantCulture)));
    }

    /// <summary>Builds the non-scope part of a cache key from a widget's effective query.</summary>
    public static string WidgetKey(
        WidgetSource source, AggregationSpec spec, DateTimeOffset fromUtc, DateTimeOffset toUtc, BucketInterval bucket)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(spec);
        var sb = new StringBuilder(128);
        sb.Append((int)source.Kind).Append('|')
          .Append(source.SavedSearchId?.ToString(CultureInfo.InvariantCulture) ?? "-").Append('|')
          .Append(source.InlineQuery ?? "-").Append('|')
          .Append(source.Metric?.ToString() ?? "-").Append('|')
          .Append((int)spec.Function).Append('|')
          .Append(spec.ValueField ?? "-").Append('|')
          .Append(spec.GroupByField ?? "-").Append('|')
          .Append((int)spec.Bucket).Append('|')
          .Append(spec.TopN).Append('|')
          .Append((int)bucket).Append('|')
          .Append(fromUtc.ToUnixTimeSeconds()).Append('|')
          .Append(toUtc.ToUnixTimeSeconds());
        return sb.ToString();
    }
}
