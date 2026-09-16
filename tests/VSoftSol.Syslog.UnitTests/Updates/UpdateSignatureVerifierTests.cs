using FluentAssertions;
using VSoftSol.Syslog.Core;
using VSoftSol.Syslog.Core.Bundles;
using VSoftSol.Syslog.Core.Updates;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Updates;

/// <summary>
/// <see cref="UpdateSignatureVerifier"/> only ever consults the one compile-time-baked
/// <see cref="ReleaseSigningInfo.PublicKeyBase64"/> — there is no way, by design, for a
/// test (or any caller) to substitute a different key. In this dev build
/// <see cref="ReleaseSigningInfo.HasRealKey"/> is false (no real release-signing key has
/// been generated for this checkout), so the only real, observable behavior to test here is
/// the fail-closed guard: signature checks never proceed, for any input, when no real key
/// is configured. The underlying ECDSA correctness (tamper detection, valid roundtrip) is
/// already covered by <c>BundleSignerTests</c>, which <see cref="UpdateSignatureVerifier"/>
/// is a thin, mechanical delegation to.
/// </summary>
[Trait("Category", "Updates")]
public sealed class UpdateSignatureVerifierTests
{
    [Fact]
    public void Verify_NoRealKeyConfigured_AlwaysFailsClosed_EvenForASelfConsistentPair()
    {
        // A document signed with a freshly generated, otherwise-valid keypair still must
        // not verify — UpdateSignatureVerifier never trusts anything but the baked-in key.
        (string publicKey, string privateKey) = BundleSigner.GenerateKeyPair();
        string document = "{\"version\":\"1.2.0\"}";
        string signature = BundleSigner.Sign(document, privateKey);

        // Sanity: BundleSigner itself, given the matching key directly, verifies this pair.
        BundleSigner.Verify(document, signature, publicKey).Should().BeTrue();

        // But UpdateSignatureVerifier ignores that key entirely.
        ReleaseSigningInfo.HasRealKey.Should().BeFalse("no real release-signing key is configured in this checkout");
        UpdateSignatureVerifier.Verify(document, signature).Should().BeFalse();
    }

    [Fact]
    public void Verify_GarbageInput_NeverThrows()
    {
        Action act = () => UpdateSignatureVerifier.Verify("not json", "not a signature");
        act.Should().NotThrow();
        UpdateSignatureVerifier.Verify("not json", "not a signature").Should().BeFalse();
    }
}
