using FluentAssertions;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Web.Security;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Security;

/// <summary>PHASE_12 build item 2 — the seeded admin's <c>password_hash IS NULL</c> is the
/// signal a fresh install has never completed the first-run wizard (CLAUDE.md: no other path
/// ever sets an initial password for that account).</summary>
[Trait("Category", "Security")]
public sealed class FirstRunStateTests
{
    [Fact]
    public async Task IsFirstRunAsync_ASeededDatabaseWithNoPasswordSet_ReturnsTrue()
    {
        await using AuthHarness harness = await AuthHarness.CreateAsync();
        var state = new FirstRunState(harness.Users);

        (await state.IsFirstRunAsync(CancellationToken.None)).Should().BeTrue();
    }

    [Fact]
    public async Task IsFirstRunAsync_AfterThePasswordIsSet_ReturnsFalse()
    {
        await using AuthHarness harness = await AuthHarness.CreateAsync();
        await harness.Users.SetPasswordAsync(
            (await harness.Users.FindByUsernameAsync("admin", CancellationToken.None))!.UserId,
            harness.Hasher.Hash("correct horse battery staple 1!"), mustChangePassword: false, harness.Clock.GetUtcNow(), CancellationToken.None);
        var state = new FirstRunState(harness.Users);

        (await state.IsFirstRunAsync(CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task MarkCompleted_Always_ShortCircuitsWithoutWaitingForTheNextQuery()
    {
        var state = new FirstRunState(null!);
        state.MarkCompleted();

        (await state.IsFirstRunAsync(CancellationToken.None)).Should().BeFalse();
    }
}
