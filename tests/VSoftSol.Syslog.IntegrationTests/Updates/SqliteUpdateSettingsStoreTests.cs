using FluentAssertions;
using VSoftSol.Syslog.Data.Updates;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Updates;

[Trait("Category", "Updates")]
public sealed class SqliteUpdateSettingsStoreTests
{
    [Fact]
    public async Task GetAsync_DefaultRow_IsDisabledWithNoKnownRelease()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteUpdateSettingsStore(db.Factory);

        UpdateSettings settings = await store.GetAsync(CancellationToken.None);

        settings.CheckEnabled.Should().BeFalse();
        settings.CheckIntervalHours.Should().Be(24);
        settings.UpdateReady.Should().BeFalse();
    }

    [Fact]
    public async Task SaveEnabledStateAsync_PersistsTheToggleAndInterval()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteUpdateSettingsStore(db.Factory);

        await store.SaveEnabledStateAsync(enabled: true, checkIntervalHours: 6, "admin", CancellationToken.None);

        UpdateSettings settings = await store.GetAsync(CancellationToken.None);
        settings.CheckEnabled.Should().BeTrue();
        settings.CheckIntervalHours.Should().Be(6);
        settings.UpdatedBy.Should().Be("admin");
    }

    [Fact]
    public async Task SaveEnabledStateAsync_ClampsAnOutOfRangeInterval()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteUpdateSettingsStore(db.Factory);

        await store.SaveEnabledStateAsync(enabled: true, checkIntervalHours: 9000, "admin", CancellationToken.None);

        (await store.GetAsync(CancellationToken.None)).CheckIntervalHours.Should().Be(168);
    }

    [Fact]
    public async Task RecordCheckResultAsync_Failure_SetsLastCheckedAndError_LeavesReadyStateAlone()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteUpdateSettingsStore(db.Factory);
        DateTimeOffset first = DateTimeOffset.UtcNow.AddMinutes(-10);
        await store.RecordUpdateReadyAsync(first, "1.2.0", "{}", "/data/updates/1.2.0/setup.msi", new string('a', 64), CancellationToken.None);

        DateTimeOffset later = DateTimeOffset.UtcNow;
        await store.RecordCheckResultAsync(later, "network unreachable", CancellationToken.None);

        UpdateSettings settings = await store.GetAsync(CancellationToken.None);
        settings.LastCheckError.Should().Be("network unreachable");
        settings.LastCheckedUtc!.Value.Should().BeCloseTo(later, TimeSpan.FromSeconds(1));
        settings.UpdateReady.Should().BeTrue("a later transient check failure must never discard an already-verified, already-staged update");
        settings.DownloadedMsiPath.Should().Be("/data/updates/1.2.0/setup.msi");
    }

    [Fact]
    public async Task RecordUpdateReadyAsync_ClearsAnyPreviousError()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteUpdateSettingsStore(db.Factory);
        await store.RecordCheckResultAsync(DateTimeOffset.UtcNow, "some earlier failure", CancellationToken.None);

        await store.RecordUpdateReadyAsync(
            DateTimeOffset.UtcNow, "1.3.0", "{\"version\":\"1.3.0\"}", "/data/updates/1.3.0/setup.msi", new string('b', 64), CancellationToken.None);

        UpdateSettings settings = await store.GetAsync(CancellationToken.None);
        settings.LastCheckError.Should().BeNull();
        settings.LatestKnownVersion.Should().Be("1.3.0");
        settings.UpdateReady.Should().BeTrue();
        settings.DownloadedMsiSha256.Should().Be(new string('b', 64));
    }
}
