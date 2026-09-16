using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Updates;

/// <summary>Reads and writes the single global self-update row (v1.1 — ADR 0021) — mirrors
/// <c>SqliteReportSmtpSettingsStore</c>.</summary>
public sealed class SqliteUpdateSettingsStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    public SqliteUpdateSettingsStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<UpdateSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT check_enabled, check_interval_hours, last_checked_utc, last_check_error,
                   latest_known_version, latest_manifest_json, downloaded_msi_path,
                   downloaded_msi_sha256, updated_utc, updated_by
            FROM update_settings WHERE id = 1;
            """;

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new UpdateSettings();
        }

        return new UpdateSettings
        {
            CheckEnabled = reader.GetInt64(0) == 1,
            CheckIntervalHours = reader.GetInt32(1),
            LastCheckedUtc = reader.IsDBNull(2) ? null : StorageFormat.ParseTimestamp(reader.GetString(2)),
            LastCheckError = reader.IsDBNull(3) ? null : reader.GetString(3),
            LatestKnownVersion = reader.IsDBNull(4) ? null : reader.GetString(4),
            LatestManifestJson = reader.IsDBNull(5) ? null : reader.GetString(5),
            DownloadedMsiPath = reader.IsDBNull(6) ? null : reader.GetString(6),
            DownloadedMsiSha256 = reader.IsDBNull(7) ? null : reader.GetString(7),
            UpdatedUtc = reader.IsDBNull(8) ? null : StorageFormat.ParseTimestamp(reader.GetString(8)),
            UpdatedBy = reader.IsDBNull(9) ? null : reader.GetString(9),
        };
    }

    /// <summary>The Administrator's own Settings-page edit. Separate from the checker's own
    /// writes below so a settings save never clobbers already-known release state.</summary>
    public async Task SaveEnabledStateAsync(bool enabled, int checkIntervalHours, string updatedBy, CancellationToken cancellationToken)
    {
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE update_settings SET
                check_enabled = $enabled, check_interval_hours = $interval, updated_utc = $now, updated_by = $by
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$interval", Math.Clamp(checkIntervalHours, 1, 168));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records that a check ran and what it found (or the error it hit), without
    /// touching the enabled/interval fields. Called by <c>UpdateChecker</c> after every
    /// attempt — success or failure.</summary>
    public async Task RecordCheckResultAsync(DateTimeOffset checkedUtc, string? error, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE update_settings SET last_checked_utc = $checked, last_check_error = $error WHERE id = 1;";
        command.Parameters.AddWithValue("$checked", StorageFormat.Timestamp(checkedUtc));
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Records a genuinely new, downloaded-and-hash-verified release. Deliberately
    /// separate from <see cref="RecordCheckResultAsync"/> — a later check that merely fails
    /// to reach GitHub (a transient network blip) must never discard an already-verified,
    /// already-staged MSI sitting on disk ready to install.</summary>
    public async Task RecordUpdateReadyAsync(
        DateTimeOffset checkedUtc, string version, string manifestJson, string downloadedMsiPath, string downloadedMsiSha256,
        CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE update_settings SET
                last_checked_utc = $checked, last_check_error = NULL, latest_known_version = $version,
                latest_manifest_json = $manifest, downloaded_msi_path = $path, downloaded_msi_sha256 = $sha
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$checked", StorageFormat.Timestamp(checkedUtc));
        command.Parameters.AddWithValue("$version", version);
        command.Parameters.AddWithValue("$manifest", manifestJson);
        command.Parameters.AddWithValue("$path", downloadedMsiPath);
        command.Parameters.AddWithValue("$sha", downloadedMsiSha256);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
