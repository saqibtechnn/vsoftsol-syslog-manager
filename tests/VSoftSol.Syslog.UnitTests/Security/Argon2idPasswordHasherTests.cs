using FluentAssertions;
using VSoftSol.Syslog.Data.Security;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Security;

/// <summary>
/// Argon2id password storage (SECURITY_STANDARDS.md V2, PHASE_04 build item 2 and the
/// "Argon2id parameter verification" security-validation item).
/// </summary>
public sealed class Argon2idPasswordHasherTests
{
    // Deliberately weak parameters so the suite stays fast. Production defaults
    // (m = 19 MiB, t = 2) are exercised by Argon2idPasswordHasherTests.DefaultOptions_*.
    private static Argon2idPasswordHasher Fast() =>
        new(new Argon2idOptions { MemoryKib = 8_192, Iterations = 1, Parallelism = 1 });

    [Fact]
    public void Hash_ProducesPhcArgon2idString_WithParameters()
    {
        string phc = Fast().Hash("correct horse battery staple");

        phc.Should().StartWith("$argon2id$v=19$m=8192,t=1,p=1$");
        phc.Split('$').Should().HaveCount(6);
    }

    [Fact]
    public void Hash_SamePasswordTwice_ProducesDifferentHashes()
    {
        Argon2idPasswordHasher hasher = Fast();

        hasher.Hash("hunter2").Should().NotBe(hasher.Hash("hunter2"), "each hash uses a fresh random salt");
    }

    [Fact]
    public void Verify_CorrectPassword_ReturnsTrue()
    {
        Argon2idPasswordHasher hasher = Fast();
        string phc = hasher.Hash("s3cr3t-passphrase");

        hasher.Verify("s3cr3t-passphrase", phc, out bool needsRehash).Should().BeTrue();
        needsRehash.Should().BeFalse();
    }

    [Fact]
    public void Verify_WrongPassword_ReturnsFalse()
    {
        Argon2idPasswordHasher hasher = Fast();
        string phc = hasher.Hash("the-right-one");

        hasher.Verify("the-wrong-one", phc, out _).Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-hash")]
    [InlineData("$argon2id$v=19$m=8192,t=1,p=1$only-four-parts")]
    [InlineData("$argon2id$v=19$m=x,t=1,p=1$c2FsdA==$aGFzaA==")]
    [InlineData("$argon2i$v=19$m=8192,t=1,p=1$c2FsdA==$aGFzaGhhc2hoYXNoaGFzaA==")]
    public void Verify_MalformedStoredHash_ReturnsFalseWithoutThrowing(string stored)
    {
        Argon2idPasswordHasher hasher = Fast();

        Action act = () => hasher.Verify("anything", stored, out _);

        act.Should().NotThrow();
        hasher.Verify("anything", stored, out _).Should().BeFalse();
    }

    [Fact]
    public void Verify_HashCreatedWithWeakerParameters_SignalsNeedsRehash()
    {
        string weak = new Argon2idPasswordHasher(
            new Argon2idOptions { MemoryKib = 8_192, Iterations = 1, Parallelism = 1 }).Hash("shared");

        var strong = new Argon2idPasswordHasher(
            new Argon2idOptions { MemoryKib = 16_384, Iterations = 3, Parallelism = 1 });

        strong.Verify("shared", weak, out bool needsRehash).Should().BeTrue();
        needsRehash.Should().BeTrue();
    }

    [Fact]
    public void DefaultOptions_MeetOwaspArgon2idMinimum()
    {
        var defaults = new Argon2idOptions();

        defaults.MemoryKib.Should().BeGreaterThanOrEqualTo(19_456);
        defaults.Iterations.Should().BeGreaterThanOrEqualTo(2);
    }
}
