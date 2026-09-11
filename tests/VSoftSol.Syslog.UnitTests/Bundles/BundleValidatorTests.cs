using FluentAssertions;
using VSoftSol.Syslog.Core.Bundles;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Bundles;

[Trait("Category", "Bundles")]
public sealed class BundleValidatorTests
{
    private static BundleHeader ValidHeader() => new(
        BundleValidator.CurrentFormatVersion, BundleKind.CustomerExport, "My export", "VSoftSol Syslog Manager", "1.0.0",
        DateTimeOffset.UtcNow, "admin");

    [Fact]
    public void ValidateHeader_AWellFormedHeader_Passes() =>
        BundleValidator.ValidateHeader(ValidHeader()).Ok.Should().BeTrue();

    [Fact]
    public void ValidateHeader_NullHeader_Fails() =>
        BundleValidator.ValidateHeader(null).Ok.Should().BeFalse();

    [Fact]
    public void ValidateHeader_WrongFormatVersion_Fails()
    {
        BundleHeader header = ValidHeader() with { FormatVersion = 99 };
        BundleValidator.ValidateHeader(header).Ok.Should().BeFalse();
    }

    [Fact]
    public void ValidateHeader_MissingTitle_Fails()
    {
        BundleHeader header = ValidHeader() with { Title = "" };
        BundleValidator.ValidateHeader(header).Ok.Should().BeFalse();
    }

    [Fact]
    public void ValidateWireSize_ADocumentOverTheCap_Fails()
    {
        string huge = new('x', BundleValidator.MaxDocumentBytes + 1);
        BundleValidator.ValidateWireSize(huge).Ok.Should().BeFalse();
    }

    [Fact]
    public void ValidateWireSize_ANormalDocument_Passes() =>
        BundleValidator.ValidateWireSize("""{"ok":true}""").Ok.Should().BeTrue();

    [Fact]
    public void ValidateSignedEnvelope_MissingSignature_Fails()
    {
        var bundle = new SignedBundle("{}", string.Empty, "pubkey", "fingerprint");
        BundleValidator.ValidateSignedEnvelope(bundle).Ok.Should().BeFalse();
    }

    [Fact]
    public void ValidateSignedEnvelope_NullBundle_Fails() =>
        BundleValidator.ValidateSignedEnvelope(null).Ok.Should().BeFalse();
}
