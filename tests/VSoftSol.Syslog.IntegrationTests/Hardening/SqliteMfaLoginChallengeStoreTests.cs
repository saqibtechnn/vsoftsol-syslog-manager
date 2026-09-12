using FluentAssertions;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hardening;

/// <summary>v1.1 (B11-3) — the login-time MFA challenge store. Dumb CRUD; attempt-limit and
/// expiry policy live in <c>AuthSessionService</c> (see <c>MfaLoginFlowTests</c>).</summary>
public sealed class SqliteMfaLoginChallengeStoreTests
{
    [Fact]
    public async Task CreateAsync_Always_ReturnsAUniqueUnguessableToken()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteMfaLoginChallengeStore(db.Factory);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string tokenA = await store.CreateAsync(1, now, TimeSpan.FromMinutes(5), CancellationToken.None);
        string tokenB = await store.CreateAsync(1, now, TimeSpan.FromMinutes(5), CancellationToken.None);

        tokenA.Should().NotBeNullOrWhiteSpace();
        tokenA.Should().NotBe(tokenB);
    }

    [Fact]
    public async Task FindAsync_AnUnknownToken_ReturnsNull()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteMfaLoginChallengeStore(db.Factory);

        (await store.FindAsync("does-not-exist", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task FindAsync_AFreshChallenge_ReturnsItWithZeroAttempts()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteMfaLoginChallengeStore(db.Factory);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        string token = await store.CreateAsync(1, now, TimeSpan.FromMinutes(5), CancellationToken.None);
        MfaLoginChallenge? challenge = await store.FindAsync(token, CancellationToken.None);

        challenge.Should().NotBeNull();
        challenge!.UserId.Should().Be(1);
        challenge.AttemptsMade.Should().Be(0);
        challenge.IsExpired(now).Should().BeFalse();
        challenge.IsExpired(now.AddMinutes(10)).Should().BeTrue();
    }

    [Fact]
    public async Task IncrementAttemptsAsync_CalledTwice_ReturnsTwo()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteMfaLoginChallengeStore(db.Factory);
        string token = await store.CreateAsync(1, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None);

        await store.IncrementAttemptsAsync(token, CancellationToken.None);
        int second = await store.IncrementAttemptsAsync(token, CancellationToken.None);

        second.Should().Be(2);
    }

    [Fact]
    public async Task DeleteAsync_Always_RemovesTheChallenge()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var store = new SqliteMfaLoginChallengeStore(db.Factory);
        string token = await store.CreateAsync(1, DateTimeOffset.UtcNow, TimeSpan.FromMinutes(5), CancellationToken.None);

        await store.DeleteAsync(token, CancellationToken.None);

        (await store.FindAsync(token, CancellationToken.None)).Should().BeNull();
    }
}
