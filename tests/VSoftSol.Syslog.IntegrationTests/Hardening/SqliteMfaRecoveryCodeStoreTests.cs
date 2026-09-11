using FluentAssertions;
using VSoftSol.Syslog.Core.Security.Mfa;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hardening;

public sealed class SqliteMfaRecoveryCodeStoreTests
{
    [Fact]
    public async Task TryConsumeAsync_TheCorrectUnusedCode_SucceedsExactlyOnce()
    {
        await using AuthHarness auth = await AuthHarness.CreateAsync();
        long userId = await auth.AddUserAsync("recovery-user", "P@ssw0rd1!");
        var store = new SqliteMfaRecoveryCodeStore(auth.Db.Factory);
        IReadOnlyList<string> codes = RecoveryCodeGenerator.Generate(10);
        await store.ReplaceAllAsync(userId, codes.Select(RecoveryCodeGenerator.Hash).ToArray(), auth.Clock.GetUtcNow(), CancellationToken.None);

        bool first = await store.TryConsumeAsync(userId, RecoveryCodeGenerator.Hash(codes[0]), auth.Clock.GetUtcNow(), CancellationToken.None);
        bool reuse = await store.TryConsumeAsync(userId, RecoveryCodeGenerator.Hash(codes[0]), auth.Clock.GetUtcNow(), CancellationToken.None);

        first.Should().BeTrue();
        reuse.Should().BeFalse("a consumed recovery code must never be usable again");
    }

    [Fact]
    public async Task TryConsumeAsync_AWrongCode_Fails()
    {
        await using AuthHarness auth = await AuthHarness.CreateAsync();
        long userId = await auth.AddUserAsync("recovery-user-2", "P@ssw0rd1!");
        var store = new SqliteMfaRecoveryCodeStore(auth.Db.Factory);
        await store.ReplaceAllAsync(userId, [RecoveryCodeGenerator.Hash("REAL-CODE-0000")], auth.Clock.GetUtcNow(), CancellationToken.None);

        bool ok = await store.TryConsumeAsync(userId, RecoveryCodeGenerator.Hash("WRONG-CODE-0000"), auth.Clock.GetUtcNow(), CancellationToken.None);

        ok.Should().BeFalse();
    }

    [Fact]
    public async Task ReplaceAllAsync_Regeneration_DiscardsThePreviousSet()
    {
        await using AuthHarness auth = await AuthHarness.CreateAsync();
        long userId = await auth.AddUserAsync("recovery-user-3", "P@ssw0rd1!");
        var store = new SqliteMfaRecoveryCodeStore(auth.Db.Factory);
        await store.ReplaceAllAsync(userId, [RecoveryCodeGenerator.Hash("OLD-CODE-0000")], auth.Clock.GetUtcNow(), CancellationToken.None);

        await store.ReplaceAllAsync(userId, [RecoveryCodeGenerator.Hash("NEW-CODE-0000")], auth.Clock.GetUtcNow(), CancellationToken.None);

        bool oldStillWorks = await store.TryConsumeAsync(userId, RecoveryCodeGenerator.Hash("OLD-CODE-0000"), auth.Clock.GetUtcNow(), CancellationToken.None);
        int unused = await store.CountUnusedAsync(userId, CancellationToken.None);

        oldStillWorks.Should().BeFalse();
        unused.Should().Be(1);
    }
}
