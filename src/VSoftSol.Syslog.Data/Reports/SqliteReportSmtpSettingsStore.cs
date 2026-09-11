using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Reports;

public sealed class SqliteReportSmtpSettingsStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    public SqliteReportSmtpSettingsStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<ReportSmtpSettings> GetAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT host, port, from_address, username, secret_name, use_tls, updated_utc, updated_by FROM report_smtp_settings WHERE id = 1;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new ReportSmtpSettings();
        }

        return new ReportSmtpSettings
        {
            Host = reader.IsDBNull(0) ? null : reader.GetString(0),
            Port = reader.GetInt32(1),
            FromAddress = reader.IsDBNull(2) ? null : reader.GetString(2),
            Username = reader.IsDBNull(3) ? null : reader.GetString(3),
            SecretName = reader.IsDBNull(4) ? null : reader.GetString(4),
            UseTls = reader.GetInt64(5) == 1,
            UpdatedUtc = reader.IsDBNull(6) ? null : StorageFormat.ParseTimestamp(reader.GetString(6)),
            UpdatedBy = reader.IsDBNull(7) ? null : reader.GetString(7),
        };
    }

    public async Task SaveAsync(ReportSmtpSettings settings, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE report_smtp_settings SET
                host = $host, port = $port, from_address = $from, username = $user,
                secret_name = $secret, use_tls = $tls, updated_utc = $now, updated_by = $by
            WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$host", (object?)settings.Host ?? DBNull.Value);
        command.Parameters.AddWithValue("$port", settings.Port);
        command.Parameters.AddWithValue("$from", (object?)settings.FromAddress ?? DBNull.Value);
        command.Parameters.AddWithValue("$user", (object?)settings.Username ?? DBNull.Value);
        command.Parameters.AddWithValue("$secret", (object?)settings.SecretName ?? DBNull.Value);
        command.Parameters.AddWithValue("$tls", settings.UseTls ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
