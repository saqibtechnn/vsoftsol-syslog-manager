using FluentAssertions;
using VSoftSol.Syslog.Core.Updates;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Updates;

[Trait("Category", "Updates")]
public sealed class UpdateManifestValidatorTests
{
    private static UpdateManifest Valid() => new(
        FormatVersion: UpdateManifestValidator.CurrentFormatVersion,
        Version: "1.2.0",
        MsiSha256: new string('a', 64),
        MsiUrl: "https://github.com/saqibtechnn/vsoftsol-syslog-manager/releases/download/v1.2.0/setup.msi",
        ReleaseNotesUrl: "https://github.com/saqibtechnn/vsoftsol-syslog-manager/releases/tag/v1.2.0",
        PublishedUtc: DateTimeOffset.UtcNow);

    [Fact]
    public void ValidateManifest_WellFormed_Succeeds()
    {
        UpdateManifestValidator.ValidateManifest(Valid()).Ok.Should().BeTrue();
    }

    [Fact]
    public void ValidateManifest_Null_Fails()
    {
        UpdateManifestValidator.ValidateManifest(null).Ok.Should().BeFalse();
    }

    [Fact]
    public void ValidateManifest_WrongFormatVersion_Fails()
    {
        UpdateManifest m = Valid() with { FormatVersion = 999 };
        UpdateManifestValidator.ValidateManifest(m).Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-a-version")]
    [InlineData("1.2")]
    public void ValidateManifest_UnparseableVersion_Fails(string version)
    {
        UpdateManifest m = Valid() with { Version = version };
        UpdateManifestValidator.ValidateManifest(m).Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("not-hex")]
    [InlineData("abc123")] // too short
    [InlineData("ABCD0000000000000000000000000000000000000000000000000000000000")] // uppercase, rejected
    public void ValidateManifest_MalformedSha256_Fails(string hash)
    {
        UpdateManifest m = Valid() with { MsiSha256 = hash };
        UpdateManifestValidator.ValidateManifest(m).Ok.Should().BeFalse();
    }

    [Theory]
    [InlineData("http://github.com/x")]      // not https
    [InlineData("not a url")]
    [InlineData("")]
    [InlineData("ftp://github.com/x")]
    public void ValidateManifest_NonHttpsMsiUrl_Fails(string url)
    {
        UpdateManifest m = Valid() with { MsiUrl = url };
        UpdateManifestValidator.ValidateManifest(m).Ok.Should().BeFalse();
    }

    [Fact]
    public void ValidateManifest_MissingPublishedUtc_Fails()
    {
        UpdateManifest m = Valid() with { PublishedUtc = default };
        UpdateManifestValidator.ValidateManifest(m).Ok.Should().BeFalse();
    }

    [Fact]
    public void ValidateWireSize_OversizedDocument_Fails()
    {
        string huge = new string('x', UpdateManifestValidator.MaxDocumentBytes + 1);
        UpdateManifestValidator.ValidateWireSize(huge).Ok.Should().BeFalse();
    }

    [Fact]
    public void ValidateWireSize_SmallDocument_Succeeds()
    {
        UpdateManifestValidator.ValidateWireSize("{}").Ok.Should().BeTrue();
    }
}
