using FluentAssertions;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Data.Retention;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Retention;

/// <summary>PHASE_10 — global retention settings + per-stream policy CRUD, and the live
/// disk-usage estimate inputs (UX_STANDARDS.md: never configure retention blind).</summary>
public sealed class RetentionPolicyStoreTests : IAsyncLifetime
{
    private RetentionTestHarness _h = null!;

    public async Task InitializeAsync() => _h = await RetentionTestHarness.CreateAsync(seed: false);

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Fact]
    public async Task GetSettingsAsync_BeforeAnySave_ReturnsTheMigrationDefaults()
    {
        RetentionSettings settings = await _h.Policies.GetSettingsAsync(CancellationToken.None);

        settings.DefaultHotDays.Should().Be(30);
        settings.DefaultWarmDays.Should().Be(90);
        settings.DefaultColdDays.Should().Be(365);
    }

    [Fact]
    public async Task SaveSettingsAsync_ThenGet_RoundTrips()
    {
        await _h.Policies.SaveSettingsAsync(
            new RetentionSettings { DefaultHotDays = 7, DefaultWarmDays = 14, DefaultColdDays = 30, ArchiveRoot = @"C:\archives", CompressionLevel = 9, BatchSize = 250 },
            "alice", CancellationToken.None);

        RetentionSettings settings = await _h.Policies.GetSettingsAsync(CancellationToken.None);

        settings.DefaultHotDays.Should().Be(7);
        settings.ArchiveRoot.Should().Be(@"C:\archives");
        settings.CompressionLevel.Should().Be(9);
        settings.BatchSize.Should().Be(250);
    }

    [Fact]
    public async Task SavePolicyAsync_ThenGet_RoundTrips()
    {
        long streamId = await _h.CreateStreamAsync("Firewall");

        await _h.Policies.SavePolicyAsync(
            new RetentionPolicy { StreamId = streamId, HotDays = 5, WarmDays = 10, ColdDays = 20, CompressionLevel = 12 }, "alice", CancellationToken.None);

        RetentionPolicy? policy = await _h.Policies.GetPolicyAsync(streamId, CancellationToken.None);

        policy.Should().NotBeNull();
        policy!.HotDays.Should().Be(5);
        policy.CompressionLevel.Should().Be(12);
    }

    [Fact]
    public async Task SavePolicyAsync_Twice_UpdatesInPlace_NoDuplicateRow()
    {
        long streamId = await _h.CreateStreamAsync("Firewall");
        await _h.Policies.SavePolicyAsync(new RetentionPolicy { StreamId = streamId, HotDays = 5, WarmDays = 10, ColdDays = 20 }, "alice", CancellationToken.None);
        await _h.Policies.SavePolicyAsync(new RetentionPolicy { StreamId = streamId, HotDays = 99, WarmDays = 10, ColdDays = 20 }, "alice", CancellationToken.None);

        IReadOnlyList<RetentionPolicy> all = await _h.Policies.ListPoliciesAsync(CancellationToken.None);

        all.Should().ContainSingle();
        all[0].HotDays.Should().Be(99);
    }

    [Fact]
    public async Task DeletePolicyAsync_RemovesTheOverride_FallingBackToGlobalDefaults()
    {
        long streamId = await _h.CreateStreamAsync("Firewall");
        await _h.Policies.SavePolicyAsync(new RetentionPolicy { StreamId = streamId, HotDays = 5, WarmDays = 10, ColdDays = 20 }, "alice", CancellationToken.None);

        bool deleted = await _h.Policies.DeletePolicyAsync(streamId, CancellationToken.None);

        deleted.Should().BeTrue();
        (await _h.Policies.GetPolicyAsync(streamId, CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task MeasureAsync_CountsOnlyHotEventsInTheLastSevenDays()
    {
        DateTimeOffset now = _h.Clock.GetUtcNow();
        await _h.SeedEventsAsync(
        [
            RetentionFixture.Event(now.AddDays(-1), "recent"),
            RetentionFixture.Event(now.AddDays(-1), "recent 2"),
            RetentionFixture.Event(now.AddDays(-30), "too old to count toward the 7-day rate"),
        ]);

        RetentionInputs inputs = await _h.Estimates.MeasureAsync(CancellationToken.None);

        inputs.EventsPerDay.Should().BeApproximately(2.0 / 7.0, 0.01);
    }
}
