using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Bundles;
using VSoftSol.Syslog.Data.Secrets;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Bundles;

public sealed record TrustedSigner(string Fingerprint, string PublicKey, string Label, DateTimeOffset TrustedUtc, string TrustedBy);

public sealed record BundleImportRecord(
    long Id, string Title, BundleKind Kind, string SignerFingerprint, string Sections, DateTimeOffset ImportedUtc, string ImportedBy);

/// <summary>
/// This installation's own bundle-signing identity, the trust-on-first-use signer list, and
/// the import history (PHASE_11 item 4, ADR 0019). The private key never leaves the
/// DPAPI-protected secret store; only the public half and its fingerprint live in the
/// plain tables here.
/// </summary>
public sealed class SqliteBundleTrustStore
{
    private const string PrivateKeySecretName = "bundle.signing.privatekey";

    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteSecretStore _secrets;

    public SqliteBundleTrustStore(SqliteConnectionFactory factory, SqliteSecretStore secrets)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    /// <summary>Returns this install's signing identity, generating one on first use.</summary>
    public async Task<(string PublicKey, string Fingerprint)> GetOrCreateIdentityAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using (SqliteConnection read = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (SqliteCommand select = read.CreateCommand())
        {
            select.CommandText = "SELECT public_key, fingerprint FROM bundle_signing_identity WHERE id = 1;";
            await using SqliteDataReader reader = await select.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return (reader.GetString(0), reader.GetString(1));
            }
        }

        (string publicKey, string privateKey) = BundleSigner.GenerateKeyPair();
        string fingerprint = BundleSigner.Fingerprint(publicKey);
        await _secrets.SetAsync(PrivateKeySecretName, privateKey, "system", cancellationToken).ConfigureAwait(false);

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand insert = connection.CreateCommand();
        insert.CommandText = """
            INSERT INTO bundle_signing_identity (id, public_key, fingerprint, created_utc) VALUES (1, $pub, $fp, $created)
            ON CONFLICT(id) DO NOTHING;
            """;
        insert.Parameters.AddWithValue("$pub", publicKey);
        insert.Parameters.AddWithValue("$fp", fingerprint);
        insert.Parameters.AddWithValue("$created", Iso(nowUtc));
        await insert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        return (publicKey, fingerprint);
    }

    /// <summary>Signs a document with this install's own private key.</summary>
    public async Task<string> SignAsync(string documentJson, CancellationToken cancellationToken)
    {
        string? privateKey = await _secrets.GetAsync(PrivateKeySecretName, cancellationToken).ConfigureAwait(false);
        if (privateKey is null)
        {
            await GetOrCreateIdentityAsync(DateTimeOffset.UtcNow, cancellationToken).ConfigureAwait(false);
            privateKey = await _secrets.GetAsync(PrivateKeySecretName, cancellationToken).ConfigureAwait(false)
                ?? throw new InvalidOperationException("Bundle signing identity could not be created.");
        }

        return BundleSigner.Sign(documentJson, privateKey);
    }

    public async Task<TrustedSigner?> FindTrustedAsync(string fingerprint, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT fingerprint, public_key, label, trusted_utc, trusted_by FROM bundle_trusted_signers WHERE fingerprint = $fp;";
        command.Parameters.AddWithValue("$fp", fingerprint);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false)
            ? new TrustedSigner(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseIso(reader.GetString(3)), reader.GetString(4))
            : null;
    }

    public async Task TrustAsync(string fingerprint, string publicKey, string label, string trustedBy, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO bundle_trusted_signers (fingerprint, public_key, label, trusted_utc, trusted_by)
            VALUES ($fp, $pub, $label, $trusted, $by)
            ON CONFLICT(fingerprint) DO UPDATE SET label = excluded.label;
            """;
        command.Parameters.AddWithValue("$fp", fingerprint);
        command.Parameters.AddWithValue("$pub", publicKey);
        command.Parameters.AddWithValue("$label", label);
        command.Parameters.AddWithValue("$trusted", Iso(nowUtc));
        command.Parameters.AddWithValue("$by", trustedBy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<TrustedSigner>> ListTrustedAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT fingerprint, public_key, label, trusted_utc, trusted_by FROM bundle_trusted_signers ORDER BY trusted_utc DESC;";
        var result = new List<TrustedSigner>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new TrustedSigner(reader.GetString(0), reader.GetString(1), reader.GetString(2), ParseIso(reader.GetString(3)), reader.GetString(4)));
        }

        return result;
    }

    public async Task RecordImportAsync(string title, BundleKind kind, string signerFingerprint, string sections, string importedBy, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO bundle_imports (title, kind, signer_fingerprint, sections, imported_utc, imported_by)
            VALUES ($title, $kind, $fp, $sections, $imported, $by);
            """;
        command.Parameters.AddWithValue("$title", title);
        command.Parameters.AddWithValue("$kind", kind == BundleKind.VendorPack ? "vendor_pack" : "customer_export");
        command.Parameters.AddWithValue("$fp", signerFingerprint);
        command.Parameters.AddWithValue("$sections", sections);
        command.Parameters.AddWithValue("$imported", Iso(nowUtc));
        command.Parameters.AddWithValue("$by", importedBy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<BundleImportRecord>> ListImportsAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT id, title, kind, signer_fingerprint, sections, imported_utc, imported_by FROM bundle_imports ORDER BY imported_utc DESC;";
        var result = new List<BundleImportRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new BundleImportRecord(
                reader.GetInt64(0), reader.GetString(1),
                reader.GetString(2) == "vendor_pack" ? BundleKind.VendorPack : BundleKind.CustomerExport,
                reader.GetString(3), reader.GetString(4), ParseIso(reader.GetString(5)), reader.GetString(6)));
        }

        return result;
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal).ToUniversalTime();
}
