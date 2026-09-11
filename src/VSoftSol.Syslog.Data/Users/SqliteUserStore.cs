using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Users;

/// <summary>
/// SQLite-backed read/write access to the <c>users</c> table and per-user scope rows.
/// Every statement is parameterised (SECURITY_STANDARDS.md §5.1). Writes take the
/// process-wide write lock; reads run on their own connection.
/// </summary>
public sealed class SqliteUserStore
{
    private const string SelectColumns =
        "u.user_id, u.username, u.display_name, u.role_id, u.password_hash, u.must_change_password, " +
        "u.is_enabled, u.failed_login_count, u.locked_until_utc, u.last_login_utc, u.password_changed_utc, u.created_utc, " +
        "u.mfa_enabled, u.mfa_enrolled_utc";

    private readonly SqliteConnectionFactory _factory;

    public SqliteUserStore(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public async Task<UserAccount?> FindByUsernameAsync(string username, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadOneAsync(connection, "WHERE u.username = $key COLLATE NOCASE", ("$key", username), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<UserAccount?> FindByIdAsync(long userId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        return await ReadOneAsync(connection, "WHERE u.user_id = $key", ("$key", userId), cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<UserAccount>> ListAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        var result = new List<UserAccount>();

        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {SelectColumns} FROM users u ORDER BY u.username COLLATE NOCASE;";
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                result.Add(Map(reader));
            }
        }

        for (int i = 0; i < result.Count; i++)
        {
            (IReadOnlyList<long> streams, IReadOnlyList<long> groups) =
                await ReadScopesAsync(connection, result[i].UserId, cancellationToken).ConfigureAwait(false);
            result[i] = result[i] with { VisibleStreamIds = streams, VisibleDeviceGroupIds = groups };
        }

        return result;
    }

    public async Task<long> CreateAsync(
        string username,
        string displayName,
        Role role,
        string? passwordHash,
        bool mustChangePassword,
        DateTimeOffset nowUtc,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(username);
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO users
              (username, display_name, role_id, password_hash, must_change_password, is_enabled,
               failed_login_count, password_changed_utc, created_utc)
            VALUES
              ($username, $display, $roleId, $hash, $mustChange, 1, 0, $pwChanged, $created);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$username", username.Trim());
        command.Parameters.AddWithValue("$display", displayName.Trim());
        command.Parameters.AddWithValue("$roleId", RoleId(role));
        command.Parameters.AddWithValue("$hash", (object?)passwordHash ?? DBNull.Value);
        command.Parameters.AddWithValue("$mustChange", mustChangePassword ? 1 : 0);
        command.Parameters.AddWithValue("$pwChanged", passwordHash is null ? DBNull.Value : Iso(nowUtc));
        command.Parameters.AddWithValue("$created", Iso(nowUtc));
        object? id = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return Convert.ToInt64(id, CultureInfo.InvariantCulture);
    }

    public async Task UpdateProfileAsync(
        long userId, string displayName, Role role, bool isEnabled, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(displayName);
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE users SET display_name = $display, role_id = $roleId, is_enabled = $enabled WHERE user_id = $id;";
        command.Parameters.AddWithValue("$display", displayName.Trim());
        command.Parameters.AddWithValue("$roleId", RoleId(role));
        command.Parameters.AddWithValue("$enabled", isEnabled ? 1 : 0);
        command.Parameters.AddWithValue("$id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task SetPasswordAsync(
        long userId, string passwordHash, bool mustChangePassword, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(passwordHash);
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE users
               SET password_hash = $hash,
                   must_change_password = $mustChange,
                   password_changed_utc = $now,
                   failed_login_count = 0,
                   locked_until_utc = NULL
             WHERE user_id = $id;
            """;
        command.Parameters.AddWithValue("$hash", passwordHash);
        command.Parameters.AddWithValue("$mustChange", mustChangePassword ? 1 : 0);
        command.Parameters.AddWithValue("$now", Iso(nowUtc));
        command.Parameters.AddWithValue("$id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordLoginSuccessAsync(long userId, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "UPDATE users SET failed_login_count = 0, locked_until_utc = NULL, last_login_utc = $now WHERE user_id = $id;";
        command.Parameters.AddWithValue("$now", Iso(nowUtc));
        command.Parameters.AddWithValue("$id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Increments the failed-login counter and, when it reaches <paramref name="lockoutThreshold"/>,
    /// sets <c>locked_until_utc</c> to now + <paramref name="lockoutDuration"/>. Returns the new count.
    /// </summary>
    public async Task<int> RecordLoginFailureAsync(
        long userId,
        DateTimeOffset nowUtc,
        int lockoutThreshold,
        TimeSpan lockoutDuration,
        CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            int count;
            await using (SqliteCommand bump = connection.CreateCommand())
            {
                bump.Transaction = transaction;
                bump.CommandText =
                    "UPDATE users SET failed_login_count = failed_login_count + 1 WHERE user_id = $id; " +
                    "SELECT failed_login_count FROM users WHERE user_id = $id;";
                bump.Parameters.AddWithValue("$id", userId);
                count = Convert.ToInt32(await bump.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
            }

            if (lockoutThreshold > 0 && count >= lockoutThreshold)
            {
                await using SqliteCommand @lock = connection.CreateCommand();
                @lock.Transaction = transaction;
                @lock.CommandText = "UPDATE users SET locked_until_utc = $until WHERE user_id = $id;";
                @lock.Parameters.AddWithValue("$until", Iso(nowUtc + lockoutDuration));
                @lock.Parameters.AddWithValue("$id", userId);
                await @lock.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            return count;
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    public async Task SetScopesAsync(
        long userId,
        IEnumerable<long> streamIds,
        IEnumerable<long> deviceGroupIds,
        CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteTransaction transaction =
            (SqliteTransaction)await connection.BeginTransactionAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await using (SqliteCommand clear = connection.CreateCommand())
            {
                clear.Transaction = transaction;
                clear.CommandText = "DELETE FROM user_scopes WHERE user_id = $id;";
                clear.Parameters.AddWithValue("$id", userId);
                await clear.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using SqliteCommand insertStream = connection.CreateCommand();
            insertStream.Transaction = transaction;
            insertStream.CommandText = "INSERT OR IGNORE INTO user_scopes (user_id, stream_id) VALUES ($id, $stream);";
            insertStream.Parameters.AddWithValue("$id", userId);
            SqliteParameter streamParam = insertStream.Parameters.Add("$stream", SqliteType.Integer);
            foreach (long streamId in streamIds ?? [])
            {
                streamParam.Value = streamId;
                await insertStream.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await using SqliteCommand insertGroup = connection.CreateCommand();
            insertGroup.Transaction = transaction;
            insertGroup.CommandText = "INSERT OR IGNORE INTO user_scopes (user_id, device_group_id) VALUES ($id, $grp);";
            insertGroup.Parameters.AddWithValue("$id", userId);
            SqliteParameter groupParam = insertGroup.Parameters.Add("$grp", SqliteType.Integer);
            foreach (long groupId in deviceGroupIds ?? [])
            {
                groupParam.Value = groupId;
                await insertGroup.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            }

            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            await transaction.RollbackAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>Turns MFA on (after the enrollment TOTP code has been verified) or off for
    /// a user. The TOTP secret itself is set/cleared separately, through the secret store.</summary>
    public async Task SetMfaEnabledAsync(long userId, bool enabled, DateTimeOffset? enrolledUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE users SET mfa_enabled = $enabled, mfa_enrolled_utc = $enrolled WHERE user_id = $id;";
        command.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
        command.Parameters.AddWithValue("$enrolled", enrolledUtc is { } e ? Iso(e) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$id", userId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<int> CountEnabledAdministratorsAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM users WHERE is_enabled = 1 AND role_id = $roleId;";
        command.Parameters.AddWithValue("$roleId", RoleId(Role.Administrator));
        return Convert.ToInt32(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    private static async Task<UserAccount?> ReadOneAsync(
        SqliteConnection connection, string whereClause, (string Name, object Value) key, CancellationToken cancellationToken)
    {
        UserAccount? account;
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = $"SELECT {SelectColumns} FROM users u {whereClause};";
            command.Parameters.AddWithValue(key.Name, key.Value);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            account = await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
        }

        if (account is null)
        {
            return null;
        }

        (IReadOnlyList<long> streams, IReadOnlyList<long> groups) =
            await ReadScopesAsync(connection, account.UserId, cancellationToken).ConfigureAwait(false);
        return account with { VisibleStreamIds = streams, VisibleDeviceGroupIds = groups };
    }

    private static async Task<(IReadOnlyList<long> Streams, IReadOnlyList<long> Groups)> ReadScopesAsync(
        SqliteConnection connection, long userId, CancellationToken cancellationToken)
    {
        var streams = new List<long>();
        var groups = new List<long>();
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            "SELECT stream_id, device_group_id FROM user_scopes WHERE user_id = $id;";
        command.Parameters.AddWithValue("$id", userId);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!reader.IsDBNull(0))
            {
                streams.Add(reader.GetInt64(0));
            }
            else if (!reader.IsDBNull(1))
            {
                groups.Add(reader.GetInt64(1));
            }
        }

        return (streams, groups);
    }

    private static UserAccount Map(SqliteDataReader reader) => new()
    {
        UserId = reader.GetInt64(0),
        Username = reader.GetString(1),
        DisplayName = reader.GetString(2),
        Role = (Role)(reader.GetInt32(3) - 1),
        PasswordHash = reader.IsDBNull(4) ? null : reader.GetString(4),
        MustChangePassword = reader.GetInt32(5) != 0,
        IsEnabled = reader.GetInt32(6) != 0,
        FailedLoginCount = reader.GetInt32(7),
        LockedUntilUtc = reader.IsDBNull(8) ? null : ParseIso(reader.GetString(8)),
        LastLoginUtc = reader.IsDBNull(9) ? null : ParseIso(reader.GetString(9)),
        PasswordChangedUtc = reader.IsDBNull(10) ? null : ParseIso(reader.GetString(10)),
        CreatedUtc = ParseIso(reader.GetString(11)),
        MfaEnabled = reader.GetInt32(12) != 0,
        MfaEnrolledUtc = reader.IsDBNull(13) ? null : ParseIso(reader.GetString(13)),
    };

    private static int RoleId(Role role) => (int)role + 1;

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);

    private static DateTimeOffset ParseIso(string value) =>
        DateTimeOffset.Parse(value, CultureInfo.InvariantCulture,
            DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal).ToUniversalTime();
}
