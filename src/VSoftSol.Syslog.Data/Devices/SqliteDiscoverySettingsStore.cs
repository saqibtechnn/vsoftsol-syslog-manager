using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Devices;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Devices;

/// <summary>The single-row discovery policy (PHASE_06 build item 2).</summary>
public sealed class SqliteDiscoverySettingsStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
{
    private readonly SqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<DiscoverySettings> GetAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT unknown_source_policy, max_pending_devices FROM discovery_settings WHERE id = 1;";
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return new DiscoverySettings();
        }

        return new DiscoverySettings
        {
            UnknownSourcePolicy = reader.GetString(0) switch
            {
                "as_unknown" => UnknownSourcePolicy.AsUnknown,
                "reject" => UnknownSourcePolicy.Reject,
                _ => UnknownSourcePolicy.AutoRegister,
            },
            MaxPendingDevices = reader.GetInt32(1),
        };
    }

    public async Task SetAsync(DiscoverySettings settings, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(settings);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE discovery_settings SET unknown_source_policy = $policy, max_pending_devices = $max,
              updated_utc = $now, updated_by = $by WHERE id = 1;
            """;
        command.Parameters.AddWithValue("$policy", settings.UnknownSourcePolicy switch
        {
            UnknownSourcePolicy.AsUnknown => "as_unknown",
            UnknownSourcePolicy.Reject => "reject",
            _ => "auto_register",
        });
        command.Parameters.AddWithValue("$max", Math.Max(1, settings.MaxPendingDevices));
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
