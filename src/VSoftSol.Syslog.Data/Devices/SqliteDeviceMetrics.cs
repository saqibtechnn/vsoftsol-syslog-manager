using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Devices;

/// <summary>Backs the device health card (PHASE_06 build item 5).</summary>
public sealed record DeviceHealth
{
    public DateTimeOffset? LastSeenUtc { get; init; }

    public long MessagesLastHour { get; init; }

    public long MessagesLast24Hours { get; init; }

    /// <summary>Per-minute counts for the last 60 minutes, oldest first — the sparkline.</summary>
    public IReadOnlyList<int> MessagesPerMinute { get; init; } = [];

    /// <summary>Fraction of the last 24 h that failed to parse (0-1).</summary>
    public double ParseFailureRate { get; init; }

    /// <summary>The most common app / tag values over the last 24 h.</summary>
    public IReadOnlyList<(string Type, long Count)> TopMessageTypes { get; init; } = [];
}

public sealed class SqliteDeviceMetrics(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
{
    private readonly SqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<DeviceHealth> GetHealthAsync(long deviceId, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        string hourAgo = StorageFormat.Timestamp(now.AddHours(-1));
        string dayAgo = StorageFormat.Timestamp(now.AddHours(-24));

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        DateTimeOffset? lastSeen = null;
        long lastHour = 0;
        long last24 = 0;
        double failureRate = 0;

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT
                    MAX(received_utc),
                    SUM(CASE WHEN received_utc >= $hour THEN 1 ELSE 0 END),
                    SUM(CASE WHEN received_utc >= $day  THEN 1 ELSE 0 END),
                    SUM(CASE WHEN received_utc >= $day AND parse_status = 'raw' THEN 1 ELSE 0 END)
                FROM events WHERE device_id = $id AND received_utc >= $day;
                """;
            command.Parameters.AddWithValue("$id", deviceId);
            command.Parameters.AddWithValue("$hour", hourAgo);
            command.Parameters.AddWithValue("$day", dayAgo);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            if (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                lastSeen = reader.IsDBNull(0) ? null : StorageFormat.ParseTimestamp(reader.GetString(0));
                lastHour = reader.IsDBNull(1) ? 0 : reader.GetInt64(1);
                last24 = reader.IsDBNull(2) ? 0 : reader.GetInt64(2);
                long failed = reader.IsDBNull(3) ? 0 : reader.GetInt64(3);
                failureRate = last24 > 0 ? (double)failed / last24 : 0;
            }
        }

        var perMinute = new int[60];
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT CAST((julianday($now) - julianday(received_utc)) * 1440 AS INTEGER) AS mins_ago, COUNT(*)
                FROM events WHERE device_id = $id AND received_utc >= $hour
                GROUP BY mins_ago;
                """;
            command.Parameters.AddWithValue("$id", deviceId);
            command.Parameters.AddWithValue("$now", StorageFormat.Timestamp(now));
            command.Parameters.AddWithValue("$hour", hourAgo);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                int minsAgo = reader.GetInt32(0);
                if (minsAgo is >= 0 and < 60)
                {
                    perMinute[59 - minsAgo] = reader.GetInt32(1);
                }
            }
        }

        var top = new List<(string, long)>();
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = """
                SELECT COALESCE(app_name, '(none)'), COUNT(*) c
                FROM events WHERE device_id = $id AND received_utc >= $day
                GROUP BY app_name ORDER BY c DESC LIMIT 5;
                """;
            command.Parameters.AddWithValue("$id", deviceId);
            command.Parameters.AddWithValue("$day", dayAgo);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                top.Add((reader.GetString(0), reader.GetInt64(1)));
            }
        }

        return new DeviceHealth
        {
            LastSeenUtc = lastSeen,
            MessagesLastHour = lastHour,
            MessagesLast24Hours = last24,
            MessagesPerMinute = perMinute,
            ParseFailureRate = failureRate,
            TopMessageTypes = top,
        };
    }
}
