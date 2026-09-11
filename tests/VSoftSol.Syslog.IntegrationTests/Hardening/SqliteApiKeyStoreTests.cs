using FluentAssertions;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hardening;

public sealed class SqliteApiKeyStoreTests
{
    [Fact]
    public async Task IsValidAsync_TheCorrectKey_NoSourceRestriction_Succeeds()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteApiKeyStore(db.Factory);
        string key = SqliteApiKeyStore.GenerateKey();
        await store.CreateAsync("Forwarder A", key, "wineventlog", allowedSourceIp: null, "admin", DateTimeOffset.UtcNow, CancellationToken.None);

        (await store.IsValidAsync("wineventlog", key, "10.0.0.9", CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsValidAsync_AWrongKey_Fails()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteApiKeyStore(db.Factory);
        await store.CreateAsync("Forwarder A", SqliteApiKeyStore.GenerateKey(), "wineventlog", null, "admin", DateTimeOffset.UtcNow, CancellationToken.None);

        (await store.IsValidAsync("wineventlog", "not-the-real-key", "10.0.0.9", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task IsValidAsync_ScopedToADifferentSourceIp_Fails_NotForged()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteApiKeyStore(db.Factory);
        string key = SqliteApiKeyStore.GenerateKey();
        await store.CreateAsync("Forwarder A", key, "wineventlog", allowedSourceIp: "10.0.0.9", "admin", DateTimeOffset.UtcNow, CancellationToken.None);

        (await store.IsValidAsync("wineventlog", key, "10.0.0.9", CancellationToken.None)).Should().BeTrue();
        (await store.IsValidAsync("wineventlog", key, "203.0.113.66", CancellationToken.None)).Should().BeFalse(
            "a key scoped to one source must not authenticate a request forged from elsewhere");
    }

    [Fact]
    public async Task IsValidAsync_ARevokedKey_Fails()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteApiKeyStore(db.Factory);
        string key = SqliteApiKeyStore.GenerateKey();
        long id = await store.CreateAsync("Forwarder A", key, "wineventlog", null, "admin", DateTimeOffset.UtcNow, CancellationToken.None);

        await store.RevokeAsync(id, CancellationToken.None);

        (await store.IsValidAsync("wineventlog", key, "10.0.0.9", CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task ListAsync_ReturnsBothActiveAndRevoked_ForThePurposeOnly()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteApiKeyStore(db.Factory);
        await store.CreateAsync("A", SqliteApiKeyStore.GenerateKey(), "wineventlog", null, "admin", DateTimeOffset.UtcNow, CancellationToken.None);
        await store.CreateAsync("B", SqliteApiKeyStore.GenerateKey(), "other", null, "admin", DateTimeOffset.UtcNow, CancellationToken.None);

        var keys = await store.ListAsync("wineventlog", CancellationToken.None);

        keys.Should().ContainSingle().Which.Label.Should().Be("A");
    }
}
