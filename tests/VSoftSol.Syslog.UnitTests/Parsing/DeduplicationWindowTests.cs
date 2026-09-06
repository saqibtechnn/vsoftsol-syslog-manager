using FluentAssertions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Ingestion.Parsing;
using VSoftSol.Syslog.UnitTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Parsing;

[Trait("Category", "Parsing")]
public sealed class DeduplicationWindowTests
{
    private static DeduplicationWindow Build(TimeSpan window, ManualClock clock) =>
        new(Options.Create(new ParsingOptions { DeduplicationWindow = window }), clock);

    [Fact]
    public void ZeroWindow_IsDisabled_AndNeverDeduplicates()
    {
        var w = Build(TimeSpan.Zero, new ManualClock());
        w.Enabled.Should().BeFalse();
        w.Record("k", 1);
        w.LookupRecent("k").Should().BeNull();
    }

    [Fact]
    public void WithinTheWindow_ARepeatIsFoldedIntoTheEarlierEvent()
    {
        var clock = new ManualClock();
        var w = Build(TimeSpan.FromSeconds(30), clock);

        w.Record("host|message", 100);

        clock.Advance(TimeSpan.FromSeconds(10));
        w.LookupRecent("host|message").Should().Be(100);
    }

    [Fact]
    public void AfterTheWindow_ARepeatIsTreatedAsNew()
    {
        var clock = new ManualClock();
        var w = Build(TimeSpan.FromSeconds(30), clock);
        w.Record("k", 100);

        clock.Advance(TimeSpan.FromSeconds(31));

        w.LookupRecent("k").Should().BeNull();
    }

    [Fact]
    public void EachLookup_RefreshesTheWindow_SoASteadyStreamKeepsDeduping()
    {
        var clock = new ManualClock();
        var w = Build(TimeSpan.FromSeconds(30), clock);
        w.Record("k", 100);

        for (int i = 0; i < 20; i++)
        {
            clock.Advance(TimeSpan.FromSeconds(20));
            w.LookupRecent("k").Should().Be(100, "iteration {0}", i);
        }
    }

    [Fact]
    public void DifferentKeys_AreIndependent()
    {
        var clock = new ManualClock();
        var w = Build(TimeSpan.FromSeconds(30), clock);
        w.Record("a", 1);
        w.Record("b", 2);

        w.LookupRecent("a").Should().Be(1);
        w.LookupRecent("b").Should().Be(2);
        w.LookupRecent("c").Should().BeNull();
    }
}
