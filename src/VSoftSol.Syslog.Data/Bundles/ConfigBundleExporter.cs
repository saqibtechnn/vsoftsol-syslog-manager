using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core;
using VSoftSol.Syslog.Core.Bundles;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Bundles;

/// <summary>
/// Builds a signed config bundle (PHASE_11 item 4) by reading each requested section
/// straight off its table — every column, generically, via the reader's own schema — so
/// this never has to be kept in lockstep with each phase's specific JSON shape. Sensitive
/// columns (only <c>users.password_hash</c>) are the one hand-maintained exclusion.
/// Extractors (vendor pattern packs) are files, not rows, and are read from
/// <see cref="PatternsDirectory"/> instead.
/// </summary>
public sealed class ConfigBundleExporter
{
    private static readonly Dictionary<string, (string Table, string[] Exclude)> TableSections = new()
    {
        [BundleSections.Rules] = ("rules", []),
        [BundleSections.Streams] = ("streams", []),
        [BundleSections.Devices] = ("devices", []),
        [BundleSections.DeviceGroups] = ("device_groups", []),
        [BundleSections.Users] = ("users", ["password_hash"]),
        [BundleSections.Dashboards] = ("dashboards", []),
        [BundleSections.Reports] = ("reports", []),
    };

    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteBundleTrustStore _trust;
    private readonly string _patternsDirectory;

    public ConfigBundleExporter(SqliteConnectionFactory factory, SqliteBundleTrustStore trust, string patternsDirectory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _patternsDirectory = patternsDirectory;
    }

    public async Task<SignedBundle> ExportAsync(
        IReadOnlySet<string> sections, BundleKind kind, string title, string createdBy, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        ArgumentException.ThrowIfNullOrWhiteSpace(createdBy);

        var sectionsJson = new Dictionary<string, string>();
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        foreach (string section in sections)
        {
            if (section == BundleSections.Extractors)
            {
                sectionsJson[section] = ExportExtractors();
                continue;
            }

            if (TableSections.TryGetValue(section, out (string Table, string[] Exclude) meta))
            {
                sectionsJson[section] = await ExportTableAsync(connection, meta.Table, meta.Exclude, cancellationToken).ConfigureAwait(false);
            }
        }

        string productVersion = typeof(ConfigBundleExporter).Assembly.GetName().Version?.ToString() ?? "0.0.0";
        var header = new BundleHeader(
            BundleValidator.CurrentFormatVersion, kind, title, BrandingInfo.ProductName, productVersion, nowUtc, createdBy);
        var envelope = new BundleEnvelope(header, sectionsJson);
        string documentJson = JsonSerializer.Serialize(envelope);

        string signature = await _trust.SignAsync(documentJson, cancellationToken).ConfigureAwait(false);
        (string publicKey, string fingerprint) = await _trust.GetOrCreateIdentityAsync(nowUtc, cancellationToken).ConfigureAwait(false);

        return new SignedBundle(documentJson, signature, publicKey, fingerprint);
    }

    private static async Task<string> ExportTableAsync(SqliteConnection connection, string table, string[] exclude, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT * FROM {table};"; // table names are compile-time constants above, never user input
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                writer.WriteStartObject();
                for (int i = 0; i < reader.FieldCount; i++)
                {
                    string name = reader.GetName(i);
                    if (exclude.Contains(name, StringComparer.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    WriteValue(writer, name, reader, i);
                }

                writer.WriteEndObject();
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static void WriteValue(Utf8JsonWriter writer, string name, SqliteDataReader reader, int ordinal)
    {
        if (reader.IsDBNull(ordinal))
        {
            writer.WriteNull(name);
            return;
        }

        object value = reader.GetValue(ordinal);
        switch (value)
        {
            case long l:
                writer.WriteNumber(name, l);
                break;
            case double d:
                writer.WriteNumber(name, d);
                break;
            case string s:
                writer.WriteString(name, s);
                break;
            case byte[] bytes:
                writer.WriteString(name, Convert.ToBase64String(bytes));
                break;
            default:
                writer.WriteString(name, value.ToString());
                break;
        }
    }

    /// <summary>Every vendor pack file under the patterns directory, as
    /// <c>{ "vendor": "...", "fileName": "...", "content": "..." }</c> — this is how a
    /// parser pack is shipped to a customer between releases (VENDOR_SUPPORT.md).</summary>
    private string ExportExtractors()
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartArray();

            // Enumerating and reading are two steps over a directory nothing locks against
            // concurrent change (an operator could delete/replace a pack while an export
            // runs) — a directory or file vanishing between them is a real, if rare,
            // transient condition, not a reason to fail the whole export.
            IEnumerable<string> vendorDirs;
            try
            {
                vendorDirs = Directory.Exists(_patternsDirectory)
                    ? Directory.EnumerateDirectories(_patternsDirectory).OrderBy(d => d, StringComparer.Ordinal).ToList()
                    : [];
            }
            catch (IOException)
            {
                vendorDirs = [];
            }

            foreach (string vendorDir in vendorDirs)
            {
                List<string> files;
                try
                {
                    files = Directory.EnumerateFiles(vendorDir, "*.pack").OrderBy(f => f, StringComparer.Ordinal).ToList();
                }
                catch (IOException)
                {
                    continue; // this vendor directory vanished after it was listed above
                }

                foreach (string file in files)
                {
                    string content;
                    try
                    {
                        content = File.ReadAllText(file);
                    }
                    catch (IOException)
                    {
                        continue; // this file vanished or was mid-write
                    }

                    writer.WriteStartObject();
                    writer.WriteString("vendor", Path.GetFileName(vendorDir));
                    writer.WriteString("fileName", Path.GetFileName(file));
                    writer.WriteString("content", content);
                    writer.WriteEndObject();
                }
            }

            writer.WriteEndArray();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }
}
