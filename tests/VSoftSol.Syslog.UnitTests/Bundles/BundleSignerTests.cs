using FluentAssertions;
using VSoftSol.Syslog.Core.Bundles;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Bundles;

[Trait("Category", "Bundles")]
public sealed class BundleSignerTests
{
    [Fact]
    public void SignThenVerify_TheSameDocument_Succeeds()
    {
        (string publicKey, string privateKey) = BundleSigner.GenerateKeyPair();
        string document = """{"header":{"title":"test"}}""";

        string signature = BundleSigner.Sign(document, privateKey);

        BundleSigner.Verify(document, signature, publicKey).Should().BeTrue();
    }

    [Fact]
    public void Verify_ATamperedDocument_Fails()
    {
        (string publicKey, string privateKey) = BundleSigner.GenerateKeyPair();
        string document = """{"header":{"title":"test"}}""";
        string signature = BundleSigner.Sign(document, privateKey);

        string tampered = document.Replace("test", "tampered");

        BundleSigner.Verify(tampered, signature, publicKey).Should().BeFalse();
    }

    [Fact]
    public void Verify_AnUntrustedSignersKey_Fails()
    {
        (string publicKey, string privateKey) = BundleSigner.GenerateKeyPair();
        (string otherPublicKey, _) = BundleSigner.GenerateKeyPair();
        string document = """{"header":{"title":"test"}}""";
        string signature = BundleSigner.Sign(document, privateKey);

        BundleSigner.Verify(document, signature, otherPublicKey).Should().BeFalse();
    }

    [Fact]
    public void Verify_AMalformedSignature_FailsWithoutThrowing()
    {
        (string publicKey, _) = BundleSigner.GenerateKeyPair();

        BundleSigner.Verify("{}", "not-a-real-signature", publicKey).Should().BeFalse();
    }

    [Fact]
    public void Fingerprint_IsStableForTheSameKey_AndDiffersAcrossKeys()
    {
        (string publicKey, _) = BundleSigner.GenerateKeyPair();
        (string otherPublicKey, _) = BundleSigner.GenerateKeyPair();

        BundleSigner.Fingerprint(publicKey).Should().Be(BundleSigner.Fingerprint(publicKey));
        BundleSigner.Fingerprint(publicKey).Should().NotBe(BundleSigner.Fingerprint(otherPublicKey));
    }
}
