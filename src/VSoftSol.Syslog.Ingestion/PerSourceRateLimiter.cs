using System.Collections.Concurrent;
using Microsoft.Extensions.Options;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// A token bucket per source IP (PHASE_02 item 9). Disabled by default
/// (<see cref="IngestionOptions.PerSourceRatePerSecond"/> = 0) because a legitimately
/// flapping device bursts hard and CLAUDE.md Constraint 3 forbids losing its messages.
/// When enabled, the breach behaviour is the operator's explicit choice; only
/// <see cref="RateLimitBreachBehavior.Drop"/> and <see cref="RateLimitBreachBehavior.Quarantine"/>
/// discard, and every discard is counted.
/// </summary>
public sealed class PerSourceRateLimiter
{
    private const int PruneEveryChecks = 50_000;
    private static readonly TimeSpan IdleEvictionAge = TimeSpan.FromMinutes(10);

    private readonly ConcurrentDictionary<string, Bucket> _buckets = new(StringComparer.Ordinal);
    private readonly TimeProvider _time;
    private readonly bool _enabled;
    private readonly double _ratePerSecond;
    private readonly double _capacity;
    private readonly RateLimitBreachBehavior _behavior;
    private readonly TimeSpan _quarantineDuration;
    private readonly double _timestampToSeconds;
    private long _checkCount;

    public PerSourceRateLimiter(IOptions<IngestionOptions> options, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        IngestionOptions o = options.Value;
        _time = timeProvider ?? TimeProvider.System;
        _ratePerSecond = o.PerSourceRatePerSecond;
        _enabled = o.PerSourceRatePerSecond > 0;
        _capacity = Math.Max(1, o.PerSourceRatePerSecond * (double)o.PerSourceBurstMultiplier);
        _behavior = o.RateLimitBreachBehavior;
        _quarantineDuration = o.QuarantineDuration;
        _timestampToSeconds = 1.0 / _time.TimestampFrequency;
    }

    public enum Decision
    {
        /// <summary>Under budget — queue it normally.</summary>
        Allow,

        /// <summary>Over budget, behaviour is Throttle — still queue it, but slow this source.</summary>
        Throttle,

        /// <summary>Over budget, behaviour is Drop or Quarantine — discard and count it.</summary>
        Drop,
    }

    /// <summary>Sources currently quarantined.</summary>
    public int QuarantinedCount
    {
        get
        {
            long now = _time.GetTimestamp();
            int n = 0;
            foreach (Bucket b in _buckets.Values)
            {
                if (Volatile.Read(ref b.QuarantinedUntil) > now)
                {
                    n++;
                }
            }

            return n;
        }
    }

    /// <summary>Charges one frame against <paramref name="sourceIp"/>'s bucket.</summary>
    public Decision Check(string sourceIp)
    {
        if (!_enabled)
        {
            return Decision.Allow;
        }

        if (Interlocked.Increment(ref _checkCount) % PruneEveryChecks == 0)
        {
            Prune();
        }

        Bucket bucket = _buckets.GetOrAdd(sourceIp, _ => new Bucket(_capacity, _time.GetTimestamp()));
        long now = _time.GetTimestamp();

        lock (bucket.Gate)
        {
            bucket.LastSeen = now;

            if (_behavior == RateLimitBreachBehavior.Quarantine && bucket.QuarantinedUntil > now)
            {
                return Decision.Drop;
            }

            double elapsedSeconds = (now - bucket.LastRefill) * _timestampToSeconds;
            if (elapsedSeconds > 0)
            {
                bucket.Tokens = Math.Min(_capacity, bucket.Tokens + (elapsedSeconds * _ratePerSecond));
                bucket.LastRefill = now;
            }

            if (bucket.Tokens >= 1.0)
            {
                bucket.Tokens -= 1.0;
                return Decision.Allow;
            }

            // Breach.
            return _behavior switch
            {
                RateLimitBreachBehavior.Throttle => Decision.Throttle,
                RateLimitBreachBehavior.Drop => Decision.Drop,
                RateLimitBreachBehavior.Quarantine => Quarantine(bucket, now),
                _ => Decision.Allow,
            };
        }
    }

    private Decision Quarantine(Bucket bucket, long now)
    {
        long until = now + (long)(_quarantineDuration.TotalSeconds * _time.TimestampFrequency);
        Volatile.Write(ref bucket.QuarantinedUntil, until);
        return Decision.Drop;
    }

    private void Prune()
    {
        long now = _time.GetTimestamp();
        long cutoff = (long)(IdleEvictionAge.TotalSeconds * _time.TimestampFrequency);
        foreach (KeyValuePair<string, Bucket> kv in _buckets)
        {
            Bucket b = kv.Value;
            if (now - Volatile.Read(ref b.LastSeen) > cutoff && Volatile.Read(ref b.QuarantinedUntil) < now)
            {
                _buckets.TryRemove(kv.Key, out _);
            }
        }
    }

    private sealed class Bucket(double tokens, long now)
    {
        public readonly object Gate = new();
        public double Tokens = tokens;
        public long LastRefill = now;
        public long LastSeen = now;
        public long QuarantinedUntil;
    }
}
