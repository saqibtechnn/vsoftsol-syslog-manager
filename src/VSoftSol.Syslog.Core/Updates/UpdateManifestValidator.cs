using System.Text;
using System.Text.RegularExpressions;

namespace VSoftSol.Syslog.Core.Updates;

/// <summary>
/// Schema and size sanity for an update manifest, checked <em>before</em> any cryptographic
/// verification or parsing is attempted — same discipline as <c>BundleValidator</c>
/// (SECURITY_STANDARDS.md: "schema-validated ... before any content is processed").
/// </summary>
public static partial class UpdateManifestValidator
{
    public const int MaxDocumentBytes = 16 * 1024; // generous for this small manifest, refuses a bomb
    public const int CurrentFormatVersion = 1;

    [GeneratedRegex("^[a-f0-9]{64}$")]
    private static partial Regex Sha256HexPattern();

    public static Bundles.BundleValidationResult ValidateWireSize(string documentJson)
    {
        ArgumentNullException.ThrowIfNull(documentJson);
        int byteCount = Encoding.UTF8.GetByteCount(documentJson);
        return byteCount > MaxDocumentBytes
            ? Bundles.BundleValidationResult.Fail($"Update manifest is {byteCount:N0} bytes, over the {MaxDocumentBytes:N0}-byte limit.")
            : Bundles.BundleValidationResult.Success;
    }

    public static Bundles.BundleValidationResult ValidateManifest(UpdateManifest? manifest)
    {
        if (manifest is null)
        {
            return Bundles.BundleValidationResult.Fail("Update manifest is empty.");
        }

        var errors = new List<string>();
        if (manifest.FormatVersion != CurrentFormatVersion)
        {
            errors.Add($"Unsupported update manifest format version {manifest.FormatVersion} (expected {CurrentFormatVersion}).");
        }

        if (string.IsNullOrWhiteSpace(manifest.Version) || !ProductVersion.TryParse(manifest.Version, out _))
        {
            errors.Add($"Update manifest has an unparseable version '{manifest.Version}'.");
        }

        if (string.IsNullOrWhiteSpace(manifest.MsiSha256) || !Sha256HexPattern().IsMatch(manifest.MsiSha256))
        {
            errors.Add("Update manifest's MsiSha256 is not a 64-character lowercase hex SHA-256 hash.");
        }

        if (!IsHttpsUrl(manifest.MsiUrl))
        {
            errors.Add("Update manifest's MsiUrl is not an absolute https URL.");
        }

        if (!IsHttpsUrl(manifest.ReleaseNotesUrl))
        {
            errors.Add("Update manifest's ReleaseNotesUrl is not an absolute https URL.");
        }

        if (manifest.PublishedUtc == default)
        {
            errors.Add("Update manifest is missing a publish timestamp.");
        }

        return errors.Count == 0 ? Bundles.BundleValidationResult.Success : new Bundles.BundleValidationResult(false, errors);
    }

    private static bool IsHttpsUrl(string? url) =>
        !string.IsNullOrWhiteSpace(url)
        && Uri.TryCreate(url, UriKind.Absolute, out Uri? parsed)
        && parsed.Scheme == Uri.UriSchemeHttps;
}
