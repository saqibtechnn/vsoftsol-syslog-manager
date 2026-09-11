using System.Text.Json;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Bundles;
using VSoftSol.Syslog.Core.Retention;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Bundles;

public sealed record BundleImportResult(bool Ok, IReadOnlyList<string> Errors, IReadOnlyDictionary<string, int> RowsAppliedBySection);

/// <summary>
/// Verifies and applies a signed config bundle (PHASE_11 item 4). Import is
/// schema-validated and signature-verified <em>before any content is processed</em>
/// (SECURITY_STANDARDS.md) — <see cref="VerifyAsync"/> is the gate every caller must pass
/// through first; <see cref="ApplyAsync"/> re-verifies rather than trusting a caller to
/// have checked. Every column name used in a generated statement comes from a fixed
/// allow-list below, never from the bundle's own JSON keys — a hostile bundle cannot turn
/// into a SQL identifier no matter what field names it invents.
/// </summary>
public sealed class ConfigBundleImporter
{
    private readonly SqliteConnectionFactory _factory;
    private readonly SqliteBundleTrustStore _trust;
    private readonly string _patternsDirectory;

    public ConfigBundleImporter(SqliteConnectionFactory factory, SqliteBundleTrustStore trust, string patternsDirectory)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _trust = trust ?? throw new ArgumentNullException(nameof(trust));
        _patternsDirectory = patternsDirectory;
    }

    public sealed record VerifiedBundle(BundleHeader Header, BundleEnvelope Envelope, string SignerFingerprint, bool SignerTrusted);

    /// <summary>Size/schema check, then signature verification against the fingerprint's
    /// public key. Never touches the database. A caller shows <see cref="VerifiedBundle.SignerTrusted"/>
    /// as a fingerprint-acceptance prompt when false (trust-on-first-use).</summary>
    public static bool TryVerify(SignedBundle bundle, out VerifiedBundle? verified, out IReadOnlyList<string> errors)
    {
        verified = null;
        var sizeCheck = BundleValidator.ValidateWireSize(bundle?.DocumentJson ?? string.Empty);
        var envelopeCheck = BundleValidator.ValidateSignedEnvelope(bundle);
        if (!sizeCheck.Ok || !envelopeCheck.Ok)
        {
            errors = [.. sizeCheck.Errors, .. envelopeCheck.Errors];
            return false;
        }

        if (!BundleSigner.Verify(bundle!.DocumentJson, bundle.SignatureBase64, bundle.PublicKeyBase64))
        {
            errors = ["Signature verification failed — the bundle does not match its claimed signer."];
            return false;
        }

        BundleEnvelope? envelope;
        try
        {
            envelope = JsonSerializer.Deserialize<BundleEnvelope>(bundle.DocumentJson);
        }
        catch (JsonException)
        {
            errors = ["Bundle document is not valid JSON."];
            return false;
        }

        var headerCheck = BundleValidator.ValidateHeader(envelope?.Header);
        if (!headerCheck.Ok)
        {
            errors = headerCheck.Errors;
            return false;
        }

        string fingerprint = BundleSigner.Fingerprint(bundle.PublicKeyBase64);
        if (fingerprint != bundle.SignerFingerprint)
        {
            errors = ["The bundle's claimed signer fingerprint does not match its own public key."];
            return false;
        }

        verified = new VerifiedBundle(envelope!.Header, envelope, fingerprint, SignerTrusted: false);
        errors = [];
        return true;
    }

    public async Task<VerifiedBundle?> VerifyAndCheckTrustAsync(SignedBundle bundle, CancellationToken cancellationToken)
    {
        if (!TryVerify(bundle, out VerifiedBundle? verified, out _))
        {
            return null;
        }

        TrustedSigner? trusted = await _trust.FindTrustedAsync(verified!.SignerFingerprint, cancellationToken).ConfigureAwait(false);
        return verified with { SignerTrusted = trusted is not null };
    }

    /// <summary>Applies every requested section inside one transaction. Refuses outright
    /// (no partial application) if verification fails or the signer is not trusted.</summary>
    public async Task<BundleImportResult> ApplyAsync(
        SignedBundle bundle, IReadOnlySet<string> sections, string importedBy, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (!TryVerify(bundle, out VerifiedBundle? verified, out IReadOnlyList<string> errors))
        {
            return new BundleImportResult(false, errors, new Dictionary<string, int>());
        }

        TrustedSigner? trusted = await _trust.FindTrustedAsync(verified!.SignerFingerprint, cancellationToken).ConfigureAwait(false);
        if (trusted is null)
        {
            return new BundleImportResult(false,
                [$"Signer {verified.SignerFingerprint} is not trusted yet. An Administrator must accept it before this bundle can be imported."],
                new Dictionary<string, int>());
        }

        var applied = new Dictionary<string, int>();
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            foreach ((string section, string json) in verified.Envelope.SectionsJson)
            {
                if (!sections.Contains(section))
                {
                    continue;
                }

                int count = section switch
                {
                    BundleSections.Rules => await ImportRulesAsync(connection, transaction, json, nowUtc, cancellationToken).ConfigureAwait(false),
                    BundleSections.Streams => await ImportStreamsAsync(connection, transaction, json, nowUtc, cancellationToken).ConfigureAwait(false),
                    BundleSections.Devices => await ImportDevicesAsync(connection, transaction, json, nowUtc, cancellationToken).ConfigureAwait(false),
                    BundleSections.DeviceGroups => await ImportDeviceGroupsAsync(connection, transaction, json, nowUtc, cancellationToken).ConfigureAwait(false),
                    BundleSections.Users => await ImportUsersAsync(connection, transaction, json, cancellationToken).ConfigureAwait(false),
                    BundleSections.Dashboards => await ImportSystemDashboardsAsync(connection, transaction, json, nowUtc, cancellationToken).ConfigureAwait(false),
                    BundleSections.Reports => await ImportSystemReportsAsync(connection, transaction, json, nowUtc, cancellationToken).ConfigureAwait(false),
                    BundleSections.Extractors => ImportExtractors(json),
                    _ => 0,
                };

                applied[section] = count;
            }

            await using (SqliteCommand log = connection.CreateCommand())
            {
                log.Transaction = transaction;
                log.CommandText = """
                    INSERT INTO bundle_imports (title, kind, signer_fingerprint, sections, imported_utc, imported_by)
                    VALUES ($title, $kind, $fp, $sections, $imported, $by);
                    """;
                log.Parameters.AddWithValue("$title", verified.Header.Title);
                log.Parameters.AddWithValue("$kind", verified.Header.Kind == BundleKind.VendorPack ? "vendor_pack" : "customer_export");
                log.Parameters.AddWithValue("$fp", verified.SignerFingerprint);
                log.Parameters.AddWithValue("$sections", string.Join(",", applied.Keys));
                log.Parameters.AddWithValue("$imported", Iso(nowUtc));
                log.Parameters.AddWithValue("$by", importedBy);
                await log.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return new BundleImportResult(true, [], applied);
        }
        catch (Exception ex)
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            return new BundleImportResult(false, [$"Import failed and was rolled back in full: {ex.Message}"], new Dictionary<string, int>());
        }
    }

    private static async Task<int> ImportRulesAsync(SqliteConnection c, SqliteTransaction t, string json, DateTimeOffset now, CancellationToken ct)
    {
        int n = 0;
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = """
            INSERT INTO rules (name, description, enabled, priority, condition_json, actions_json, stop_processing, created_utc, updated_utc)
            VALUES ($name, $desc, $enabled, $priority, $cond, $actions, $stop, $created, $updated)
            ON CONFLICT(name) DO UPDATE SET
                description = excluded.description, enabled = excluded.enabled, priority = excluded.priority,
                condition_json = excluded.condition_json, actions_json = excluded.actions_json,
                stop_processing = excluded.stop_processing, updated_utc = excluded.updated_utc;
            """;
        foreach (JsonElement row in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$name", Str(row, "name") ?? continueSkip());
            cmd.Parameters.AddWithValue("$desc", (object?)Str(row, "description") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$enabled", IntOrDefault(row, "enabled", 1));
            cmd.Parameters.AddWithValue("$priority", IntOrDefault(row, "priority", 100));
            cmd.Parameters.AddWithValue("$cond", (object?)Str(row, "condition_json") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$actions", (object?)Str(row, "actions_json") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$stop", IntOrDefault(row, "stop_processing", 0));
            cmd.Parameters.AddWithValue("$created", Str(row, "created_utc") ?? Iso(now));
            cmd.Parameters.AddWithValue("$updated", Iso(now));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            n++;
        }

        return n;

        static string continueSkip() => throw new InvalidDataException("A rule row is missing its 'name' field.");
    }

    private static async Task<int> ImportStreamsAsync(SqliteConnection c, SqliteTransaction t, string json, DateTimeOffset now, CancellationToken ct)
    {
        int n = 0;
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = """
            INSERT INTO streams (name, description, match_json, is_system, enabled, sort_order, created_utc)
            VALUES ($name, $desc, $match, 0, $enabled, $sort, $created)
            ON CONFLICT(name) DO UPDATE SET
                description = excluded.description, match_json = excluded.match_json,
                enabled = excluded.enabled, sort_order = excluded.sort_order;
            """;
        foreach (JsonElement row in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            string? name = Str(row, "name");
            if (name is null || IntOrDefault(row, "is_catch_all", 0) == 1)
            {
                continue; // the catch-all stream is seeded locally and never imported
            }

            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$desc", (object?)Str(row, "description") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$match", (object?)Str(row, "match_json") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$enabled", IntOrDefault(row, "enabled", 1));
            cmd.Parameters.AddWithValue("$sort", IntOrDefault(row, "sort_order", 0));
            cmd.Parameters.AddWithValue("$created", Str(row, "created_utc") ?? Iso(now));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    private static async Task<int> ImportDevicesAsync(SqliteConnection c, SqliteTransaction t, string json, DateTimeOffset now, CancellationToken ct)
    {
        int n = 0;
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = """
            INSERT INTO devices (name, primary_ip, hostname, vendor, notes, timezone, discovered, created_utc)
            VALUES ($name, $ip, $host, $vendor, $notes, $tz, 0, $created)
            ON CONFLICT(name) DO UPDATE SET
                primary_ip = excluded.primary_ip, hostname = excluded.hostname, vendor = excluded.vendor,
                notes = excluded.notes, timezone = excluded.timezone;
            """;
        foreach (JsonElement row in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            string? name = Str(row, "name");
            if (name is null)
            {
                continue;
            }

            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$ip", (object?)Str(row, "primary_ip") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$host", (object?)Str(row, "hostname") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$vendor", (object?)Str(row, "vendor") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$notes", (object?)Str(row, "notes") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$tz", (object?)Str(row, "timezone") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", Str(row, "created_utc") ?? Iso(now));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    private static async Task<int> ImportDeviceGroupsAsync(SqliteConnection c, SqliteTransaction t, string json, DateTimeOffset now, CancellationToken ct)
    {
        int n = 0;
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = """
            INSERT INTO device_groups (name, description, created_utc) VALUES ($name, $desc, $created)
            ON CONFLICT(name) DO UPDATE SET description = excluded.description;
            """;
        foreach (JsonElement row in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            string? name = Str(row, "name");
            if (name is null)
            {
                continue;
            }

            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$name", name);
            cmd.Parameters.AddWithValue("$desc", (object?)Str(row, "description") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$created", Str(row, "created_utc") ?? Iso(now));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    /// <summary>Users import without a password hash (never in a bundle) — the account is
    /// created disabled from logging in with a password until an Administrator sets one;
    /// <c>must_change_password</c> is forced so the first successful login requires it.</summary>
    private static async Task<int> ImportUsersAsync(SqliteConnection c, SqliteTransaction t, string json, CancellationToken ct)
    {
        int n = 0;
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = """
            INSERT INTO users (username, display_name, role_id, is_enabled, must_change_password, failed_login_count, created_utc)
            VALUES ($username, $display, $role, $enabled, 1, 0, $created)
            ON CONFLICT(username) DO UPDATE SET display_name = excluded.display_name, role_id = excluded.role_id;
            """;
        foreach (JsonElement row in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            string? username = Str(row, "username");
            if (username is null)
            {
                continue;
            }

            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$username", username);
            cmd.Parameters.AddWithValue("$display", Str(row, "display_name") ?? username);
            cmd.Parameters.AddWithValue("$role", IntOrDefault(row, "role_id", 3)); // default ReadOnly (role_id = Role+1)
            cmd.Parameters.AddWithValue("$enabled", IntOrDefault(row, "is_enabled", 1));
            cmd.Parameters.AddWithValue("$created", DateTimeOffset.UtcNow.ToString("O"));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    /// <summary>Only <c>is_system</c> dashboards import — a personal dashboard's ownership
    /// cannot be remapped across installs without a user-identity bridge this format does
    /// not have (documented limitation, known-issues.md B11-1).</summary>
    private static async Task<int> ImportSystemDashboardsAsync(SqliteConnection c, SqliteTransaction t, string json, DateTimeOffset now, CancellationToken ct)
    {
        int n = 0;
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = """
            INSERT INTO dashboards (owner_user_id, name, description, is_shared, is_system, system_key,
                default_range_seconds, refresh_seconds, widgets_json, layout_json, created_utc, updated_utc)
            VALUES (NULL, $name, $desc, 1, 1, $key, $range, $refresh, $widgets, $layout, $created, $updated)
            ON CONFLICT(system_key) DO UPDATE SET
                name = excluded.name, description = excluded.description,
                widgets_json = excluded.widgets_json, layout_json = excluded.layout_json, updated_utc = excluded.updated_utc;
            """;
        foreach (JsonElement row in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            if (IntOrDefault(row, "is_system", 0) != 1 || Str(row, "system_key") is not { } key)
            {
                continue;
            }

            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$name", Str(row, "name") ?? key);
            cmd.Parameters.AddWithValue("$desc", (object?)Str(row, "description") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$key", key);
            cmd.Parameters.AddWithValue("$range", IntOrDefault(row, "default_range_seconds", 86400));
            cmd.Parameters.AddWithValue("$refresh", IntOrDefault(row, "refresh_seconds", 0));
            cmd.Parameters.AddWithValue("$widgets", Str(row, "widgets_json") ?? "[]");
            cmd.Parameters.AddWithValue("$layout", Str(row, "layout_json") ?? "[]");
            cmd.Parameters.AddWithValue("$created", Str(row, "created_utc") ?? Iso(now));
            cmd.Parameters.AddWithValue("$updated", Iso(now));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    /// <summary>Only <c>is_system</c> reports import, matched by <c>template_key</c> — the
    /// same ownership-mapping limitation as dashboards applies to personal reports.</summary>
    private static async Task<int> ImportSystemReportsAsync(SqliteConnection c, SqliteTransaction t, string json, DateTimeOffset now, CancellationToken ct)
    {
        int n = 0;
        await using SqliteCommand cmd = c.CreateCommand();
        cmd.Transaction = t;
        cmd.CommandText = """
            INSERT INTO reports (name, template_key, query_text, time_range_days, schedule, delivery_json, owner_user_id, is_system, enabled, created_utc)
            VALUES ($name, $key, $query, $range, $schedule, $delivery, NULL, 1, $enabled, $created)
            ON CONFLICT(template_key) WHERE is_system = 1 DO UPDATE SET
                name = excluded.name, query_text = excluded.query_text, time_range_days = excluded.time_range_days;
            """;
        foreach (JsonElement row in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            if (IntOrDefault(row, "is_system", 0) != 1)
            {
                continue;
            }

            cmd.Parameters.Clear();
            cmd.Parameters.AddWithValue("$name", Str(row, "name") ?? "Imported report");
            cmd.Parameters.AddWithValue("$key", Str(row, "template_key") ?? "custom");
            cmd.Parameters.AddWithValue("$query", (object?)Str(row, "query_text") ?? DBNull.Value);
            cmd.Parameters.AddWithValue("$range", IntOrDefault(row, "time_range_days", 90));
            cmd.Parameters.AddWithValue("$schedule", Str(row, "schedule") ?? "none");
            cmd.Parameters.AddWithValue("$delivery", Str(row, "delivery_json") ?? "{}");
            cmd.Parameters.AddWithValue("$enabled", IntOrDefault(row, "enabled", 1));
            cmd.Parameters.AddWithValue("$created", Str(row, "created_utc") ?? Iso(now));
            await cmd.ExecuteNonQueryAsync(ct).ConfigureAwait(false);
            n++;
        }

        return n;
    }

    /// <summary>Writes each pack file under its vendor sub-directory — a runtime-loaded
    /// drop-in, no recompilation (VENDOR_SUPPORT.md). Vendor/file names are sanitised with
    /// the same <see cref="ArchiveNaming"/> the Phase 10 archive format uses (stripped of
    /// separators and leading dots), then re-verified with <see cref="ArchiveNaming.IsSafeUnderRoot"/>
    /// before any write — zip-slip cannot occur here for the same reason it cannot in an
    /// archive: the path is rebuilt from sanitised segments, never trusted as given.</summary>
    private int ImportExtractors(string json)
    {
        int n = 0;
        string root = Path.GetFullPath(_patternsDirectory);
        foreach (JsonElement row in JsonDocument.Parse(json).RootElement.EnumerateArray())
        {
            string? vendor = Str(row, "vendor");
            string? fileName = Str(row, "fileName");
            string? content = Str(row, "content");
            if (vendor is null || fileName is null || content is null)
            {
                continue;
            }

            string safeVendor = ArchiveNaming.SanitizeSegment(vendor);
            string safeFile = ArchiveNaming.SanitizeSegment(fileName);
            if (safeVendor.Length == 0 || safeFile.Length == 0 || !safeFile.EndsWith(".pack", StringComparison.OrdinalIgnoreCase))
            {
                continue;
            }

            string dir = Path.GetFullPath(Path.Combine(root, safeVendor));
            string path = Path.GetFullPath(Path.Combine(dir, safeFile));
            if (!ArchiveNaming.IsSafeUnderRoot(root, path))
            {
                continue;
            }

            Directory.CreateDirectory(dir);
            File.WriteAllText(path, content);
            n++;
        }

        return n;
    }

    private static string? Str(JsonElement row, string name) =>
        row.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static int IntOrDefault(JsonElement row, string name, int fallback) =>
        row.TryGetProperty(name, out JsonElement v) && v.ValueKind == JsonValueKind.Number ? v.GetInt32() : fallback;

    private static string Iso(DateTimeOffset value) => value.ToUniversalTime().UtcDateTime.ToString("O");
}
