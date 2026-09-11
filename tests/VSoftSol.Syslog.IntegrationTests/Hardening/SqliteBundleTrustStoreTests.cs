using System.Runtime.Versioning;
using FluentAssertions;
using VSoftSol.Syslog.Core.Bundles;
using VSoftSol.Syslog.Data.Bundles;
using VSoftSol.Syslog.Data.Secrets;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hardening;

[SupportedOSPlatform("windows")]
public sealed class SqliteBundleTrustStoreTests
{
    private static SqliteBundleTrustStore NewStore(SqliteTestDatabase db) =>
        new(db.Factory, new SqliteSecretStore(db.Factory, new DpapiSecretProtector()));

    [Fact]
    public async Task GetOrCreateIdentityAsync_IsStable_AcrossCalls()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        SqliteBundleTrustStore store = NewStore(db);

        (string pub1, string fp1) = await store.GetOrCreateIdentityAsync(DateTimeOffset.UtcNow, CancellationToken.None);
        (string pub2, string fp2) = await store.GetOrCreateIdentityAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        pub1.Should().Be(pub2);
        fp1.Should().Be(fp2);
    }

    [Fact]
    public async Task SignAsync_ProducesASignatureThatVerifiesAgainstTheStoredIdentity()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        SqliteBundleTrustStore store = NewStore(db);
        (string publicKey, _) = await store.GetOrCreateIdentityAsync(DateTimeOffset.UtcNow, CancellationToken.None);

        string document = """{"header":{"title":"export"}}""";
        string signature = await store.SignAsync(document, CancellationToken.None);

        BundleSigner.Verify(document, signature, publicKey).Should().BeTrue();
    }

    [Fact]
    public async Task TrustAsync_ThenFindTrustedAsync_RoundTrips()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        SqliteBundleTrustStore store = NewStore(db);

        await store.TrustAsync("AA:BB:CC", "pub-key-text", "Vendor Pack Publisher", "admin", DateTimeOffset.UtcNow, CancellationToken.None);
        TrustedSigner? found = await store.FindTrustedAsync("AA:BB:CC", CancellationToken.None);

        found.Should().NotBeNull();
        found!.Label.Should().Be("Vendor Pack Publisher");
    }

    [Fact]
    public async Task FindTrustedAsync_AnUntrustedFingerprint_ReturnsNull()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        SqliteBundleTrustStore store = NewStore(db);

        (await store.FindTrustedAsync("never-trusted", CancellationToken.None)).Should().BeNull();
    }

    [Fact]
    public async Task RecordImportAsync_ThenListImportsAsync_ShowsTheHistory()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        SqliteBundleTrustStore store = NewStore(db);

        await store.RecordImportAsync("My export", BundleKind.CustomerExport, "AA:BB", "rules,streams", "admin", DateTimeOffset.UtcNow, CancellationToken.None);

        IReadOnlyList<BundleImportRecord> imports = await store.ListImportsAsync(CancellationToken.None);

        imports.Should().ContainSingle(i => i.Title == "My export" && i.Kind == BundleKind.CustomerExport);
    }
}
