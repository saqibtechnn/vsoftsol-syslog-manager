using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.Data.Users;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>
/// Composes the Phase 4 authentication stack over a throwaway seeded database with a
/// controllable clock and deliberately weak Argon2 parameters (so the suite stays fast —
/// production defaults are checked separately in the unit tests).
/// </summary>
public sealed class AuthHarness : IAsyncDisposable
{
    private AuthHarness(SqliteTestDatabase db, FakeTimeProvider clock, AuthenticationOptions options)
    {
        Db = db;
        Clock = clock;
        Options = options;
        Hasher = new Argon2idPasswordHasher(new Argon2idOptions { MemoryKib = 8_192, Iterations = 1, Parallelism = 1 });
        Users = new SqliteUserStore(db.Factory);
        Auth = new LocalAuthenticationProvider(Users, Hasher, Microsoft.Extensions.Options.Options.Create(options), clock);
    }

    public SqliteTestDatabase Db { get; }

    public FakeTimeProvider Clock { get; }

    public AuthenticationOptions Options { get; }

    public Argon2idPasswordHasher Hasher { get; }

    public SqliteUserStore Users { get; }

    public LocalAuthenticationProvider Auth { get; }

    public static async Task<AuthHarness> CreateAsync(Action<AuthenticationOptions>? configure = null)
    {
        SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
        var clock = new FakeTimeProvider(new DateTimeOffset(2026, 9, 7, 12, 0, 0, TimeSpan.Zero));
        var options = new AuthenticationOptions();
        configure?.Invoke(options);
        return new AuthHarness(db, clock, options);
    }

    public async Task<long> AddUserAsync(
        string username,
        string password,
        Role role = Role.Operator,
        bool mustChangePassword = false,
        bool enabled = true)
    {
        long id = await Users.CreateAsync(
            username, username, role, Hasher.Hash(password), mustChangePassword, Clock.GetUtcNow(), CancellationToken.None);
        if (!enabled)
        {
            await Users.UpdateProfileAsync(id, username, role, isEnabled: false, CancellationToken.None);
        }

        return id;
    }

    public ValueTask DisposeAsync() => Db.DisposeAsync();
}
