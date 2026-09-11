using System.Runtime.Versioning;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Bundles;
using VSoftSol.Syslog.Data.Bundles;
using VSoftSol.Syslog.Data.Secrets;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hardening;

/// <summary>
/// PHASE_11 item 4 — export/import round trip and the bundle-attack matrix (malformed
/// signature, untrusted signer, oversized document, path-traversal in an extractor file
/// name). XXE and zip-slip are not separately fuzzed here: the format is pure JSON with no
/// zip container anywhere, so neither attack class exists to exploit — the same "provably
/// absent by construction" defence as the Phase 10 archive format.
/// </summary>
[SupportedOSPlatform("windows")]
public sealed class ConfigBundleTests
{
    private static readonly IReadOnlySet<string> AllSections = new HashSet<string>(BundleSections.All);

    private static (ConfigBundleExporter Exporter, ConfigBundleImporter Importer, SqliteBundleTrustStore Trust) NewBundleStack(
        SqliteTestDatabase db, string patternsDir)
    {
        var trust = new SqliteBundleTrustStore(db.Factory, new SqliteSecretStore(db.Factory, new DpapiSecretProtector()));
        return (new ConfigBundleExporter(db.Factory, trust, patternsDir), new ConfigBundleImporter(db.Factory, trust, patternsDir), trust);
    }

    /// <summary>A fresh, uniquely-named directory — never the shared system temp root
    /// directly. `ExportExtractors` enumerates its patterns directory's subdirectories;
    /// under xUnit's parallel execution, passing the bare shared temp root let another
    /// test's own temp-database directory (created and deleted concurrently, unrelated to
    /// patterns) race `Directory.EnumerateDirectories` and throw
    /// `DirectoryNotFoundException` — a test-isolation bug (TESTING_STANDARDS.md §2.4),
    /// not a product defect. Caught by the flaky failure in the first full-suite run.</summary>
    private static string NewTempPatternsDir() =>
        Path.Combine(Path.GetTempPath(), "vsoftsol-patterns-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task RoundTrip_ExportFromA_ImportToCleanB_RulesStreamsDevicesReappearIdentically()
    {
        string patternsA = Path.Combine(Path.GetTempPath(), "vsoftsol-patterns-a-" + Guid.NewGuid().ToString("N"));
        string patternsB = Path.Combine(Path.GetTempPath(), "vsoftsol-patterns-b-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(patternsA, "acme-widget"));
        await File.WriteAllTextAsync(Path.Combine(patternsA, "acme-widget", "acme-widget.pack"), "[pack]\nvendor = acme-widget\n");

        try
        {
            await using SqliteTestDatabase a = await SqliteTestDatabase.CreateSeededAsync();
            (ConfigBundleExporter exporter, _, _) = NewBundleStack(a, patternsA);

            await Exec(a, "INSERT INTO streams (name, description, enabled, created_utc) VALUES ('vpn-events', 'VPN logs', 1, 't');");
            await Exec(a, "INSERT INTO device_groups (name, description, created_utc) VALUES ('branch-offices', 'Branches', 't');");
            await Exec(a, "INSERT INTO devices (name, primary_ip, vendor, created_utc) VALUES ('edge-fw-01', '10.0.0.1', 'fortigate', 't');");
            await Exec(a, "INSERT INTO rules (name, description, priority, created_utc, updated_utc) VALUES ('alert-on-vpn-fail', 'x', 50, 't', 't');");

            SignedBundle bundle = await exporter.ExportAsync(AllSections, BundleKind.CustomerExport, "A's export", "admin", DateTimeOffset.UtcNow, CancellationToken.None);

            await using SqliteTestDatabase b = await SqliteTestDatabase.CreateSeededAsync();
            (_, ConfigBundleImporter importer, SqliteBundleTrustStore trustB) = NewBundleStack(b, patternsB);

            // Trust-on-first-use: an Administrator must accept a new signer before import.
            ConfigBundleImporter.TryVerify(bundle, out ConfigBundleImporter.VerifiedBundle? verified, out _).Should().BeTrue();
            await trustB.TrustAsync(verified!.SignerFingerprint, bundle.PublicKeyBase64, "A's install", "admin", DateTimeOffset.UtcNow, CancellationToken.None);

            BundleImportResult result = await importer.ApplyAsync(bundle, AllSections, "admin", DateTimeOffset.UtcNow, CancellationToken.None);

            result.Ok.Should().BeTrue(string.Join("; ", result.Errors));
            (await Scalar(b, "SELECT COUNT(*) FROM streams WHERE name = 'vpn-events';")).Should().Be(1L);
            (await Scalar(b, "SELECT COUNT(*) FROM device_groups WHERE name = 'branch-offices';")).Should().Be(1L);
            (await Scalar(b, "SELECT vendor FROM devices WHERE name = 'edge-fw-01';")).Should().Be("fortigate");
            (await Scalar(b, "SELECT priority FROM rules WHERE name = 'alert-on-vpn-fail';")).Should().Be(50L);
            File.Exists(Path.Combine(patternsB, "acme-widget", "acme-widget.pack")).Should().BeTrue("extractors ship as bundles too");
        }
        finally
        {
            if (Directory.Exists(patternsA))
            {
                Directory.Delete(patternsA, recursive: true);
            }

            if (Directory.Exists(patternsB))
            {
                Directory.Delete(patternsB, recursive: true);
            }
        }
    }

    [Fact]
    public async Task ApplyAsync_AnUntrustedSigner_IsRefused_BeforeAnyContentIsProcessed()
    {
        await using SqliteTestDatabase a = await SqliteTestDatabase.CreateSeededAsync();
        (ConfigBundleExporter exporter, _, _) = NewBundleStack(a, NewTempPatternsDir());
        await Exec(a, "INSERT INTO streams (name, enabled, created_utc) VALUES ('never-imported', 1, 't');");
        SignedBundle bundle = await exporter.ExportAsync(AllSections, BundleKind.CustomerExport, "x", "admin", DateTimeOffset.UtcNow, CancellationToken.None);

        await using SqliteTestDatabase b = await SqliteTestDatabase.CreateSeededAsync();
        (_, ConfigBundleImporter importer, _) = NewBundleStack(b, NewTempPatternsDir());

        BundleImportResult result = await importer.ApplyAsync(bundle, AllSections, "admin", DateTimeOffset.UtcNow, CancellationToken.None);

        result.Ok.Should().BeFalse();
        (await Scalar(b, "SELECT COUNT(*) FROM streams WHERE name = 'never-imported';")).Should().Be(0L);
    }

    [Fact]
    public async Task ApplyAsync_ATamperedDocument_FailsSignatureVerification()
    {
        await using SqliteTestDatabase a = await SqliteTestDatabase.CreateSeededAsync();
        (ConfigBundleExporter exporter, _, _) = NewBundleStack(a, NewTempPatternsDir());
        SignedBundle bundle = await exporter.ExportAsync(AllSections, BundleKind.CustomerExport, "x", "admin", DateTimeOffset.UtcNow, CancellationToken.None);
        SignedBundle tampered = bundle with { DocumentJson = bundle.DocumentJson.Replace("\"x\"", "\"tampered\"") };

        await using SqliteTestDatabase b = await SqliteTestDatabase.CreateSeededAsync();
        (_, ConfigBundleImporter importer, SqliteBundleTrustStore trustB) = NewBundleStack(b, NewTempPatternsDir());
        ConfigBundleImporter.TryVerify(bundle, out ConfigBundleImporter.VerifiedBundle? verified, out _);
        await trustB.TrustAsync(verified!.SignerFingerprint, bundle.PublicKeyBase64, "x", "admin", DateTimeOffset.UtcNow, CancellationToken.None);

        BundleImportResult result = await importer.ApplyAsync(tampered, AllSections, "admin", DateTimeOffset.UtcNow, CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Errors.Should().Contain(e => e.Contains("Signature"));
    }

    [Fact]
    public void TryVerify_AnOversizedDocument_IsRefused()
    {
        var huge = new SignedBundle(new string('x', BundleValidator.MaxDocumentBytes + 1), "sig", "pub", "fp");

        ConfigBundleImporter.TryVerify(huge, out _, out IReadOnlyList<string> errors).Should().BeFalse();
        errors.Should().NotBeEmpty();
    }

    [Fact]
    public void TryVerify_AMalformedSignature_IsRefused()
    {
        var bad = new SignedBundle("""{"header":{}}""", "not-base64!!", "not-a-key", "fp");

        ConfigBundleImporter.TryVerify(bad, out _, out IReadOnlyList<string> errors).Should().BeFalse();
    }

    [Fact]
    public async Task ImportExtractors_APathTraversalFileName_NeverEscapesThePatternsRoot()
    {
        string patternsRoot = Path.Combine(Path.GetTempPath(), "vsoftsol-patterns-root-" + Guid.NewGuid().ToString("N"));
        string patternsDir = Path.Combine(patternsRoot, "patterns");
        Directory.CreateDirectory(patternsDir);
        try
        {
            await using SqliteTestDatabase db = await SqliteTestDatabase.CreateSeededAsync();
            var trust = new SqliteBundleTrustStore(db.Factory, new SqliteSecretStore(db.Factory, new DpapiSecretProtector()));
            var importer = new ConfigBundleImporter(db.Factory, trust, patternsDir);

            (string publicKey, string fingerprint) = await trust.GetOrCreateIdentityAsync(DateTimeOffset.UtcNow, CancellationToken.None);
            await trust.TrustAsync(fingerprint, publicKey, "self", "admin", DateTimeOffset.UtcNow, CancellationToken.None);

            // Both fields try to walk up and out of the patterns root with "../../".
            string sectionsJson = """[{"vendor":"../../../../evil","fileName":"../../evil.pack","content":"[pack]"}]""";
            var header = new BundleHeader(BundleValidator.CurrentFormatVersion, BundleKind.VendorPack, "evil", "p", "1.0.0", DateTimeOffset.UtcNow, "attacker");
            var envelope = new BundleEnvelope(header, new Dictionary<string, string> { [BundleSections.Extractors] = sectionsJson });
            string documentJson = System.Text.Json.JsonSerializer.Serialize(envelope);
            string signature = await trust.SignAsync(documentJson, CancellationToken.None);
            var bundle = new SignedBundle(documentJson, signature, publicKey, fingerprint);

            BundleImportResult result = await importer.ApplyAsync(bundle, new HashSet<string> { BundleSections.Extractors }, "admin", DateTimeOffset.UtcNow, CancellationToken.None);

            result.Ok.Should().BeTrue();
            // Whatever (sanitised) file this produced, it must be strictly inside patternsDir —
            // nothing at all should appear one level up, where a real escape would land.
            Directory.GetFiles(patternsRoot, "evil.pack", SearchOption.AllDirectories).Should().BeEmpty();
            foreach (string file in Directory.GetFiles(patternsDir, "*", SearchOption.AllDirectories))
            {
                Path.GetFullPath(file).Should().StartWith(Path.GetFullPath(patternsDir));
            }
        }
        finally
        {
            Directory.Delete(patternsRoot, recursive: true);
        }
    }

    private static async Task<object?> Scalar(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return await command.ExecuteScalarAsync(CancellationToken.None);
    }

    private static async Task Exec(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync(CancellationToken.None);
    }
}
