using System.Text;

namespace VSoftSol.Syslog.Core.Bundles;

public sealed record BundleValidationResult(bool Ok, IReadOnlyList<string> Errors)
{
    public static BundleValidationResult Success { get; } = new(true, []);

    public static BundleValidationResult Fail(string error) => new(false, [error]);
}

/// <summary>
/// Schema and size sanity for a bundle, checked <em>before</em> any cryptographic
/// verification or content processing is attempted (SECURITY_STANDARDS.md: "schema-
/// validated and signature-verified before any content is processed"). This layer never
/// parses XML and never opens a zip — the bundle format has neither, so XXE and zip-slip
/// cannot occur by construction, the same defence used for the Phase 10 archive format.
/// </summary>
public static class BundleValidator
{
    public const int MaxDocumentBytes = 100 * 1024 * 1024; // 100 MB — generous for a JSON export, refuses a bomb
    public const int CurrentFormatVersion = 1;

    public static BundleValidationResult ValidateWireSize(string documentJson)
    {
        ArgumentNullException.ThrowIfNull(documentJson);
        int byteCount = Encoding.UTF8.GetByteCount(documentJson);
        return byteCount > MaxDocumentBytes
            ? BundleValidationResult.Fail($"Bundle document is {byteCount:N0} bytes, over the {MaxDocumentBytes:N0}-byte limit.")
            : BundleValidationResult.Success;
    }

    public static BundleValidationResult ValidateHeader(BundleHeader? header)
    {
        if (header is null)
        {
            return BundleValidationResult.Fail("Bundle has no header.");
        }

        var errors = new List<string>();
        if (header.FormatVersion != CurrentFormatVersion)
        {
            errors.Add($"Unsupported bundle format version {header.FormatVersion} (expected {CurrentFormatVersion}).");
        }

        if (!Enum.IsDefined(header.Kind))
        {
            errors.Add("Bundle header names an unrecognised kind.");
        }

        if (string.IsNullOrWhiteSpace(header.Title))
        {
            errors.Add("Bundle header is missing a title.");
        }

        if (header.CreatedUtc == default)
        {
            errors.Add("Bundle header is missing a creation timestamp.");
        }

        return errors.Count == 0 ? BundleValidationResult.Success : new BundleValidationResult(false, errors);
    }

    public static BundleValidationResult ValidateSignedEnvelope(SignedBundle? bundle)
    {
        if (bundle is null)
        {
            return BundleValidationResult.Fail("Bundle payload is empty.");
        }

        var errors = new List<string>();
        if (string.IsNullOrWhiteSpace(bundle.DocumentJson))
        {
            errors.Add("Bundle document is empty.");
        }

        if (string.IsNullOrWhiteSpace(bundle.SignatureBase64))
        {
            errors.Add("Bundle signature is missing.");
        }

        if (string.IsNullOrWhiteSpace(bundle.PublicKeyBase64))
        {
            errors.Add("Bundle signer public key is missing.");
        }

        return errors.Count == 0 ? BundleValidationResult.Success : new BundleValidationResult(false, errors);
    }
}
