using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Notifications;

/// <summary>A notification for the UI notification centre (PHASE_07 "RaiseNotification").</summary>
public sealed record NotificationRow(
    long NotificationId,
    NotificationLevel Level,
    string Title,
    string Body,
    string Source,
    long? RuleId,
    DateTimeOffset CreatedUtc,
    DateTimeOffset? ReadUtc);

/// <summary>
/// Stores UI notifications raised by rules (and, later, the system). Text is stored
/// verbatim — encoding happens at render (Constraint 9), the same as every other
/// wire-derived field.
/// </summary>
public sealed class SqliteNotificationStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
{
    private readonly SqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public async Task<long> RaiseAsync(
        NotificationLevel level, string title, string body, long? ruleId, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(title);
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO notifications (level, title, body, source, rule_id, created_utc)
            VALUES ($level, $title, $body, $source, $rule, $now);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$level", LevelToken(level));
        command.Parameters.AddWithValue("$title", Cap(title, 300));
        command.Parameters.AddWithValue("$body", Cap(body ?? string.Empty, 4000));
        command.Parameters.AddWithValue("$source", ruleId is null ? "system" : "rule");
        command.Parameters.AddWithValue("$rule", (object?)ruleId ?? DBNull.Value);
        command.Parameters.AddWithValue("$now", StorageFormat.Timestamp(_time.GetUtcNow()));
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public async Task<IReadOnlyList<NotificationRow>> ListActiveAsync(int limit, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT notification_id, level, title, body, source, rule_id, created_utc, read_utc
              FROM notifications
             WHERE dismissed_utc IS NULL
             ORDER BY created_utc DESC
             LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$limit", Math.Clamp(limit, 1, 500));

        var rows = new List<NotificationRow>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            rows.Add(new NotificationRow(
                reader.GetInt64(0),
                ParseLevel(reader.GetString(1)),
                reader.GetString(2),
                reader.GetString(3),
                reader.GetString(4),
                reader.IsDBNull(5) ? null : reader.GetInt64(5),
                StorageFormat.ParseTimestamp(reader.GetString(6)),
                StorageFormat.ParseTimestampOrNull(reader.IsDBNull(7) ? null : reader.GetString(7))));
        }

        return rows;
    }

    public async Task<long> CountUnreadAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "SELECT COUNT(*) FROM notifications WHERE dismissed_utc IS NULL AND read_utc IS NULL;";
        return Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            System.Globalization.CultureInfo.InvariantCulture);
    }

    public Task MarkReadAsync(long notificationId, CancellationToken cancellationToken) =>
        StampAsync(
            "UPDATE notifications SET read_utc = $now WHERE notification_id = $id AND read_utc IS NULL;",
            notificationId, cancellationToken);

    public Task DismissAsync(long notificationId, CancellationToken cancellationToken) =>
        StampAsync(
            "UPDATE notifications SET dismissed_utc = $now WHERE notification_id = $id AND dismissed_utc IS NULL;",
            notificationId, cancellationToken);

    public async Task MarkAllReadAsync(CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE notifications SET read_utc = $now WHERE read_utc IS NULL AND dismissed_utc IS NULL;";
        command.Parameters.AddWithValue("$now", StorageFormat.Timestamp(_time.GetUtcNow()));
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private async Task StampAsync(string sql, long id, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("$now", StorageFormat.Timestamp(_time.GetUtcNow()));
        command.Parameters.AddWithValue("$id", id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    private static string LevelToken(NotificationLevel level) => level switch
    {
        NotificationLevel.Info => "info",
        NotificationLevel.Critical => "critical",
        _ => "warning",
    };

    private static NotificationLevel ParseLevel(string token) => token switch
    {
        "info" => NotificationLevel.Info,
        "critical" => NotificationLevel.Critical,
        _ => NotificationLevel.Warning,
    };

    private static string Cap(string value, int max) => value.Length <= max ? value : value[..max];
}
