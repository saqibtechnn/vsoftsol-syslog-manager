using System.Security.Cryptography;
using System.Text.Json;
using VSoftSol.Syslog.Core.Bundles;
using VSoftSol.Syslog.Core.Updates;

namespace VSoftSol.Syslog.ReleaseSigning;

/// <summary>
/// Vendor-only CLI for the self-update trust anchor (v1.1 — ADR 0021). Never shipped, never
/// referenced by the product — see this project's own doc comment on why. Two subcommands:
/// <c>generate-keys</c> (once, offline) and <c>sign-release</c> (once per published release).
/// See README.md for the full manual release-cut checklist.
/// </summary>
public static class Program
{
    private const string PrivateKeyEnvVar = "VSOFTSOL_RELEASE_PRIVATE_KEY";

    public static int Main(string[] args)
    {
        if (args.Length == 0)
        {
            return Usage();
        }

        try
        {
            return args[0] switch
            {
                "generate-keys" => GenerateKeys(args),
                "sign-release" => SignRelease(args),
                _ => Usage(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or FormatException or CryptographicException)
        {
            Console.Error.WriteLine($"Error: {ex.Message}");
            return 2;
        }
    }

    private static int Usage()
    {
        Console.Error.WriteLine("""
            Usage:
              generate-keys <repo-root>
                  Generates a new ECDSA P-256 keypair. Prints the PRIVATE key to the console
                  only (never written to disk) and writes the PUBLIC key to
                  <repo-root>/release-signing/public-key.txt.

              sign-release <msi-path> <version> <msi-url> <release-notes-url> [private-key]
                  Hashes the MSI, builds and signs an update manifest, and writes
                  update-manifest.json next to the MSI. The private key may be given as the
                  last argument or via the VSOFTSOL_RELEASE_PRIVATE_KEY environment variable
                  (never both, never neither).
            """);
        return 1;
    }

    private static int GenerateKeys(string[] args)
    {
        if (args.Length != 2)
        {
            return Usage();
        }

        string repoRoot = args[1];
        (string publicKey, string privateKey) = BundleSigner.GenerateKeyPair();

        string releaseSigningDir = Path.Combine(repoRoot, "release-signing");
        Directory.CreateDirectory(releaseSigningDir);
        File.WriteAllText(Path.Combine(releaseSigningDir, "public-key.txt"), publicKey);

        Console.WriteLine("Public key written to release-signing/public-key.txt (safe to commit).");
        Console.WriteLine();
        Console.WriteLine("PRIVATE KEY — copy this into your own offline secure storage now.");
        Console.WriteLine("It is never written to disk by this tool and cannot be recovered if lost:");
        Console.WriteLine();
        Console.WriteLine(privateKey);
        Console.WriteLine();
        Console.WriteLine("Clear your terminal scrollback after copying it.");
        return 0;
    }

    private static int SignRelease(string[] args)
    {
        if (args.Length is not (5 or 6))
        {
            return Usage();
        }

        string msiPath = args[1];
        string version = args[2];
        string msiUrl = args[3];
        string releaseNotesUrl = args[4];
        string? privateKeyArg = args.Length == 6 ? args[5] : null;
        string? privateKeyEnv = Environment.GetEnvironmentVariable(PrivateKeyEnvVar);

        if (privateKeyArg is not null && privateKeyEnv is not null)
        {
            Console.Error.WriteLine($"Error: pass the private key either as an argument or via {PrivateKeyEnvVar}, not both.");
            return 1;
        }

        string? privateKey = privateKeyArg ?? privateKeyEnv;
        if (privateKey is null)
        {
            Console.Error.WriteLine($"Error: no private key given (argument or {PrivateKeyEnvVar}).");
            return 1;
        }

        if (!File.Exists(msiPath))
        {
            Console.Error.WriteLine($"Error: '{msiPath}' does not exist.");
            return 1;
        }

        if (!ProductVersion.TryParse(version, out _))
        {
            Console.Error.WriteLine($"Error: '{version}' is not a parseable version.");
            return 1;
        }

        byte[] msiBytes = File.ReadAllBytes(msiPath);
        string msiSha256 = Convert.ToHexString(SHA256.HashData(msiBytes)).ToLowerInvariant();

        var manifest = new UpdateManifest(
            UpdateManifestValidator.CurrentFormatVersion,
            version,
            msiSha256,
            msiUrl,
            releaseNotesUrl,
            DateTimeOffset.UtcNow);

        Core.Bundles.BundleValidationResult schemaCheck = UpdateManifestValidator.ValidateManifest(manifest);
        if (!schemaCheck.Ok)
        {
            Console.Error.WriteLine("Error: the manifest built from these arguments failed validation:");
            foreach (string error in schemaCheck.Errors)
            {
                Console.Error.WriteLine($"  - {error}");
            }

            return 1;
        }

        string documentJson = JsonSerializer.Serialize(manifest, JsonOptions);
        string signatureBase64 = BundleSigner.Sign(documentJson, privateKey);
        var signed = new SignedUpdateManifest(documentJson, signatureBase64);
        string signedJson = JsonSerializer.Serialize(signed, JsonOptions);

        string outputPath = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(msiPath)) ?? ".", "update-manifest.json");
        File.WriteAllText(outputPath, signedJson);

        Console.WriteLine($"Signed manifest for version {version} written to {outputPath}.");
        Console.WriteLine($"MSI SHA-256: {msiSha256}");
        Console.WriteLine("Upload both the MSI and update-manifest.json as assets on the GitHub release.");
        return 0;
    }

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);
}
