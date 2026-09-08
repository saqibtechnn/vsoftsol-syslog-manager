using System.Globalization;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Logging;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Sqlite;
using VSoftSol.Syslog.Data.Streams;

namespace VSoftSol.Syslog.Data.Seed;

/// <summary>
/// Idempotently inserts the data the product needs to function with zero configuration:
/// the four fixed roles, a seeded administrator (password set by the first-run wizard),
/// and the seven default streams (PHASE_06). Safe to run on every startup.
/// </summary>
public sealed class DatabaseSeeder
{
    /// <summary>Username of the seeded administrator account.</summary>
    public const string SeededAdminUsername = "admin";

    private static readonly IReadOnlyList<DefaultStreamRules.Entry> DefaultStreams = DefaultStreamRules.All;

    private readonly SqliteConnectionFactory _factory;
    private readonly ILogger<DatabaseSeeder> _logger;

    public DatabaseSeeder(SqliteConnectionFactory factory, ILogger<DatabaseSeeder> logger)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public async Task SeedAsync(CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);

        string nowUtc = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture);

        try
        {
            foreach (Role role in Enum.GetValues<Role>())
            {
                await ExecuteAsync(connection, transaction, """
                    INSERT INTO roles (role_id, name, is_system) VALUES ($id, $name, 1)
                    ON CONFLICT(role_id) DO NOTHING;
                    """, cancellationToken,
                    ("$id", (int)role + 1),
                    ("$name", role.ToString()));
            }

            await ExecuteAsync(connection, transaction, """
                INSERT INTO users (username, display_name, role_id, password_hash, must_change_password, is_enabled, created_utc)
                VALUES ($username, $display, $roleId, NULL, 1, 1, $created)
                ON CONFLICT(username) DO NOTHING;
                """, cancellationToken,
                ("$username", SeededAdminUsername),
                ("$display", "Administrator"),
                ("$roleId", (int)Role.Administrator + 1),
                ("$created", nowUtc));

            for (int i = 0; i < DefaultStreams.Count; i++)
            {
                DefaultStreamRules.Entry stream = DefaultStreams[i];
                string? matchJson = stream.Match is null ? null : StreamMatchJson.Serialize(stream.Match);

                await ExecuteAsync(connection, transaction, """
                    INSERT INTO streams (name, is_system, is_catch_all, enabled, sort_order, match_json, created_utc)
                    VALUES ($name, 1, $catchAll, 1, $order, $match, $created)
                    ON CONFLICT(name) DO NOTHING;
                    """, cancellationToken,
                    ("$name", stream.Name),
                    ("$catchAll", stream.IsCatchAll ? 1 : 0),
                    ("$order", i),
                    ("$match", (object?)matchJson ?? DBNull.Value),
                    ("$created", nowUtc));

                // Backfill the rule for a stream seeded before migration 004 (match_json still NULL);
                // never overwrite an operator's edit.
                await ExecuteAsync(connection, transaction, """
                    UPDATE streams SET is_catch_all = $catchAll, match_json = $match
                    WHERE name = $name AND is_system = 1 AND match_json IS NULL AND is_catch_all = 0;
                    """, cancellationToken,
                    ("$name", stream.Name),
                    ("$catchAll", stream.IsCatchAll ? 1 : 0),
                    ("$match", (object?)matchJson ?? DBNull.Value));
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            _logger.LogInformation("Database seed verified: {RoleCount} roles, seeded admin, {StreamCount} default streams.",
                Enum.GetValues<Role>().Length, DefaultStreams.Count);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private static async Task ExecuteAsync(
        SqliteConnection connection,
        SqliteTransaction transaction,
        string sql,
        CancellationToken cancellationToken,
        params (string Name, object Value)[] parameters)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.Transaction = transaction;
        command.CommandText = sql;
        foreach ((string name, object value) in parameters)
        {
            command.Parameters.AddWithValue(name, value);
        }

        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }
}
