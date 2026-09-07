using System.Diagnostics;
using FluentAssertions;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Seed;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Auth;

/// <summary>
/// PHASE_04: Argon2id local authentication, account lockout, and the constant-time
/// enumeration-resistant failure path (SECURITY_STANDARDS.md "username enumeration via
/// timing and error text", "brute force past the lockout").
/// </summary>
public sealed class LocalAuthenticationProviderTests
{
    [Fact]
    public async Task AuthenticateAsync_CorrectPassword_ReturnsSuccessWithRoleAndMustChangeFlag()
    {
        await using AuthHarness h = await AuthHarness.CreateAsync();
        await h.AddUserAsync("alice", "correct horse battery staple", Role.Operator, mustChangePassword: true);

        AuthenticationResult result = await h.Auth.AuthenticateAsync("alice", "correct horse battery staple", CancellationToken.None);

        result.Succeeded.Should().BeTrue();
        result.User!.Role.Should().Be(Role.Operator);
        result.User.MustChangePassword.Should().BeTrue();
    }

    [Fact]
    public async Task AuthenticateAsync_UnknownUser_ReturnsUnknownUserFailure()
    {
        await using AuthHarness h = await AuthHarness.CreateAsync();

        AuthenticationResult result = await h.Auth.AuthenticateAsync("nobody", "whatever", CancellationToken.None);

        result.Succeeded.Should().BeFalse();
        result.FailureReason.Should().Be(AuthenticationFailureReason.UnknownUser);
    }

    [Fact]
    public async Task AuthenticateAsync_WrongPassword_ReturnsBadPasswordAndIncrementsFailureCount()
    {
        await using AuthHarness h = await AuthHarness.CreateAsync();
        long id = await h.AddUserAsync("bob", "the-real-password");

        AuthenticationResult result = await h.Auth.AuthenticateAsync("bob", "guess-1", CancellationToken.None);

        result.FailureReason.Should().Be(AuthenticationFailureReason.BadPassword);
        (await h.Users.FindByIdAsync(id, CancellationToken.None))!.FailedLoginCount.Should().Be(1);
    }

    [Fact]
    public async Task AuthenticateAsync_AfterThresholdConsecutiveFailures_LocksTheAccount()
    {
        await using AuthHarness h = await AuthHarness.CreateAsync(o =>
        {
            o.LockoutThreshold = 3;
            o.LockoutDuration = TimeSpan.FromMinutes(15);
        });
        await h.AddUserAsync("carol", "s3cret-passphrase");

        for (int i = 0; i < 3; i++)
        {
            await h.Auth.AuthenticateAsync("carol", "wrong", CancellationToken.None);
        }

        AuthenticationResult afterLock = await h.Auth.AuthenticateAsync("carol", "s3cret-passphrase", CancellationToken.None);
        afterLock.Succeeded.Should().BeFalse();
        afterLock.FailureReason.Should().Be(AuthenticationFailureReason.AccountLocked,
            "a correct password must not succeed while the account is locked");
    }

    [Fact]
    public async Task AuthenticateAsync_WhenLockoutDurationHasElapsed_AllowsLoginAgain()
    {
        await using AuthHarness h = await AuthHarness.CreateAsync(o =>
        {
            o.LockoutThreshold = 2;
            o.LockoutDuration = TimeSpan.FromMinutes(10);
        });
        await h.AddUserAsync("dave", "right-password-here");
        await h.Auth.AuthenticateAsync("dave", "x", CancellationToken.None);
        await h.Auth.AuthenticateAsync("dave", "x", CancellationToken.None); // now locked

        h.Clock.Advance(TimeSpan.FromMinutes(11));

        AuthenticationResult result = await h.Auth.AuthenticateAsync("dave", "right-password-here", CancellationToken.None);
        result.Succeeded.Should().BeTrue();
    }

    [Fact]
    public async Task AuthenticateAsync_SuccessfulLogin_ResetsTheFailureCount()
    {
        await using AuthHarness h = await AuthHarness.CreateAsync(o => o.LockoutThreshold = 5);
        long id = await h.AddUserAsync("erin", "erins-passphrase");
        await h.Auth.AuthenticateAsync("erin", "nope", CancellationToken.None);
        await h.Auth.AuthenticateAsync("erin", "nope", CancellationToken.None);

        await h.Auth.AuthenticateAsync("erin", "erins-passphrase", CancellationToken.None);

        (await h.Users.FindByIdAsync(id, CancellationToken.None))!.FailedLoginCount.Should().Be(0);
    }

    [Fact]
    public async Task AuthenticateAsync_DisabledAccount_ReturnsAccountDisabled()
    {
        await using AuthHarness h = await AuthHarness.CreateAsync();
        await h.AddUserAsync("frank", "frank-passphrase", enabled: false);

        AuthenticationResult result = await h.Auth.AuthenticateAsync("frank", "frank-passphrase", CancellationToken.None);

        result.FailureReason.Should().Be(AuthenticationFailureReason.AccountDisabled);
    }

    [Fact]
    public async Task AuthenticateAsync_SeededAdminBeforeWizardSetsPassword_FailsWithoutThrowing()
    {
        await using AuthHarness h = await AuthHarness.CreateAsync();

        Func<Task<AuthenticationResult>> act = () =>
            h.Auth.AuthenticateAsync(DatabaseSeeder.SeededAdminUsername, "anything", CancellationToken.None);

        AuthenticationResult result = (await act.Should().NotThrowAsync()).Subject;
        result.Succeeded.Should().BeFalse();
    }

    [Fact]
    public async Task RefreshAsync_DisabledUser_ReturnsNull()
    {
        await using AuthHarness h = await AuthHarness.CreateAsync();
        await h.AddUserAsync("grace", "grace-passphrase", enabled: false);

        (await h.Auth.RefreshAsync("grace", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task AuthenticateAsync_UnknownUserVsWrongPassword_TakeComparableTime()
    {
        // The enumeration-resistance property: both paths spend one Argon2 computation, so
        // a timing side channel cannot distinguish "no such user" from "bad password".
        await using AuthHarness h = await AuthHarness.CreateAsync(o => o.LockoutThreshold = 0);
        await h.AddUserAsync("heidi", "heidis-real-passphrase");

        // warm up
        await h.Auth.AuthenticateAsync("heidi", "warm", CancellationToken.None);
        await h.Auth.AuthenticateAsync("ghost", "warm", CancellationToken.None);

        TimeSpan known = await Median(() => h.Auth.AuthenticateAsync("heidi", "bad", CancellationToken.None));
        TimeSpan unknown = await Median(() => h.Auth.AuthenticateAsync("ghost-user", "bad", CancellationToken.None));

        double ratio = known.TotalMilliseconds / Math.Max(1.0, unknown.TotalMilliseconds);
        ratio.Should().BeInRange(0.5, 2.0,
            "the unknown-user and bad-password paths must not differ by more than ~2x");
    }

    private static async Task<TimeSpan> Median(Func<Task> action)
    {
        var samples = new List<double>();
        for (int i = 0; i < 7; i++)
        {
            var sw = Stopwatch.StartNew();
            await action();
            sw.Stop();
            samples.Add(sw.Elapsed.TotalMilliseconds);
        }

        samples.Sort();
        return TimeSpan.FromMilliseconds(samples[samples.Count / 2]);
    }
}
