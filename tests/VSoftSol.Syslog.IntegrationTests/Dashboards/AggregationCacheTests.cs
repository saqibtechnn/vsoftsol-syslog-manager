using FluentAssertions;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Dashboards;

/// <summary>
/// PHASE_09 build item 8 + security — the aggregation cache. TTL is honoured, a time-range
/// change invalidates (by changing the key), the key is fingerprinted by scope so one
/// user's result can never be served to another, and a concurrent burst computes once.
/// </summary>
public sealed class AggregationCacheTests
{
    private static readonly UserScope ScopeA = UserScope.Create([1, 2], null);
    private static readonly UserScope ScopeB = UserScope.Create([3], null);

    [Fact]
    public async Task GetOrAdd_WithinTheTtl_DoesNotReinvokeTheFactory()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var cache = new AggregationCache(clock, () => TimeSpan.FromSeconds(15));
        int calls = 0;

        Task<string> Factory(CancellationToken _) { calls++; return Task.FromResult("v" + calls); }

        (await cache.GetOrAddAsync(ScopeA, "k", Factory, CancellationToken.None)).Should().Be("v1");
        clock.Advance(TimeSpan.FromSeconds(10));
        (await cache.GetOrAddAsync(ScopeA, "k", Factory, CancellationToken.None)).Should().Be("v1");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrAdd_AfterTheTtl_Recomputes()
    {
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 6, 1, 0, 0, 0, TimeSpan.Zero));
        var cache = new AggregationCache(clock, () => TimeSpan.FromSeconds(15));
        int calls = 0;
        Task<string> Factory(CancellationToken _) { calls++; return Task.FromResult("v" + calls); }

        await cache.GetOrAddAsync(ScopeA, "k", Factory, CancellationToken.None);
        clock.Advance(TimeSpan.FromSeconds(16));
        (await cache.GetOrAddAsync(ScopeA, "k", Factory, CancellationToken.None)).Should().Be("v2");
    }

    [Fact]
    public async Task GetOrAdd_ForADifferentScope_NeverServesTheOtherScopesResult()
    {
        var clock = new FakeTimeProvider();
        var cache = new AggregationCache(clock, () => TimeSpan.FromSeconds(30));

        string a = await cache.GetOrAddAsync(ScopeA, "same-key", _ => Task.FromResult("A-DATA"), CancellationToken.None);
        string b = await cache.GetOrAddAsync(ScopeB, "same-key", _ => Task.FromResult("B-DATA"), CancellationToken.None);

        a.Should().Be("A-DATA");
        b.Should().Be("B-DATA");
    }

    [Fact]
    public async Task GetOrAdd_ForADifferentTimeRangeKey_Recomputes()
    {
        var clock = new FakeTimeProvider();
        var cache = new AggregationCache(clock, () => TimeSpan.FromSeconds(30));

        string first = await cache.GetOrAddAsync(ScopeA, "range=1h", _ => Task.FromResult("1h"), CancellationToken.None);
        string second = await cache.GetOrAddAsync(ScopeA, "range=24h", _ => Task.FromResult("24h"), CancellationToken.None);

        first.Should().Be("1h");
        second.Should().Be("24h");
    }

    [Fact]
    public async Task GetOrAdd_Under20ConcurrentCallers_InvokesTheFactoryOnce()
    {
        var clock = new FakeTimeProvider();
        var cache = new AggregationCache(clock, () => TimeSpan.FromSeconds(30));
        int calls = 0;

        async Task<string> Factory(CancellationToken ct)
        {
            Interlocked.Increment(ref calls);
            await Task.Delay(30, ct);
            return "shared";
        }

        string[] results = await Task.WhenAll(Enumerable.Range(0, 20)
            .Select(_ => cache.GetOrAddAsync(ScopeA, "hot", Factory, CancellationToken.None)));

        results.Should().OnlyContain(r => r == "shared");
        calls.Should().Be(1);
    }

    [Fact]
    public async Task GetOrAdd_WhenTheFactoryThrows_DoesNotCacheTheFailure()
    {
        var clock = new FakeTimeProvider();
        var cache = new AggregationCache(clock, () => TimeSpan.FromSeconds(30));
        int calls = 0;

        Task<string> Factory(CancellationToken _)
        {
            calls++;
            return calls == 1 ? Task.FromException<string>(new InvalidOperationException("boom")) : Task.FromResult("ok");
        }

        Func<Task> first = () => cache.GetOrAddAsync(ScopeA, "k", Factory, CancellationToken.None);
        await first.Should().ThrowAsync<InvalidOperationException>();

        (await cache.GetOrAddAsync(ScopeA, "k", Factory, CancellationToken.None)).Should().Be("ok");
    }

    [Fact]
    public void ScopeFingerprint_IsStableForEqualScopes_AndDistinctForDifferent()
    {
        AggregationCache.ScopeFingerprint(UserScope.Create([2, 1], null))
            .Should().Be(AggregationCache.ScopeFingerprint(UserScope.Create([1, 2], null)));

        AggregationCache.ScopeFingerprint(UserScope.Unrestricted)
            .Should().NotBe(AggregationCache.ScopeFingerprint(UserScope.Create([1], null)));

        AggregationCache.ScopeFingerprint(UserScope.Create(null, [5]))
            .Should().NotBe(AggregationCache.ScopeFingerprint(UserScope.Create([5], null)));
    }
}
