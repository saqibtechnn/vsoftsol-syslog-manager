namespace VSoftSol.Syslog.Core.Bundles;

/// <summary>
/// Who a bundle claims to be from. A <see cref="VendorPack"/> is verified against the
/// product's embedded publisher key; a <see cref="CustomerExport"/> is verified against a
/// trust-on-first-use signer the Administrator explicitly accepted on a previous import
/// (see <c>docs/adr/0019-hardening.md</c> for the rationale — there is no PKI here, and a
/// symmetric key shared between installs would let anyone who obtains it forge either kind).
/// </summary>
public enum BundleKind
{
    VendorPack = 0,
    CustomerExport = 1,
}

/// <summary>The eight exportable content sections (PHASE_11 item 4). A bundle carries any
/// non-empty subset — an operator exporting "just the rules" ships only that section.</summary>
public static class BundleSections
{
    public const string Rules = "rules";
    public const string Streams = "streams";
    public const string Devices = "devices";
    public const string DeviceGroups = "device_groups";
    public const string Users = "users";
    public const string Dashboards = "dashboards";
    public const string Reports = "reports";
    public const string Extractors = "extractors";

    public static readonly IReadOnlyList<string> All =
        [Rules, Streams, Devices, DeviceGroups, Users, Dashboards, Reports, Extractors];
}

/// <summary>The bundle's metadata, carried inside the signed document so tampering with it
/// invalidates the signature exactly like tampering with the content would.</summary>
public sealed record BundleHeader(
    int FormatVersion,
    BundleKind Kind,
    string Title,
    string ProductName,
    string ProductVersion,
    DateTimeOffset CreatedUtc,
    string CreatedBy);

/// <summary>
/// The unsigned document: a header plus one raw JSON array of entries per populated
/// section. Kept as raw section text (rather than deserializing into full domain records at
/// this layer) so Core never needs to know the shape of a rule/stream/dashboard — that
/// belongs to Data, which builds and consumes this envelope.
/// </summary>
public sealed record BundleEnvelope(BundleHeader Header, IReadOnlyDictionary<string, string> SectionsJson);

/// <summary>The wire form: the envelope's canonical JSON text plus an ECDSA P-256
/// signature over those exact bytes, the signer's public key, and its fingerprint (so the
/// import UI can show "this bundle is signed by fingerprint AB12:CD34:..." without first
/// deciding whether to trust it).</summary>
public sealed record SignedBundle(string DocumentJson, string SignatureBase64, string PublicKeyBase64, string SignerFingerprint);
