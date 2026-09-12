using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Listeners;

/// <summary>One row from the <c>listeners</c> table (migration 001) — the identity <c>events.listener_id</c> points at.</summary>
public sealed record ListenerRecord(
    long ListenerId, string Name, Protocol Protocol, string BindAddress, int Port, bool Enabled, DateTimeOffset CreatedUtc);

/// <summary>
/// v1.1 — P2-1 (`docs/evidence/phase-02/known-issues.md`): the <c>listeners</c> table
/// existed since Phase 1 but nothing ever wrote to it, so <c>events.listener_id</c> was
/// always NULL. <see cref="UpsertAsync"/> is called once per protocol at collector startup
/// (<c>ListenerRegistrationHostedService</c>) and again after a live UDP/TCP port change
/// (<c>ListenerPortReloadService</c>). Keying the upsert on <c>name</c> — which already
/// encodes protocol/bind/port, exactly matching each <c>ISyslogListener</c>'s own naming
/// convention — means an unchanged restart reuses the same id, while a changed port
/// (a live rebind, or a config edit) is a genuinely different listener identity and gets a
/// new row, leaving already-stored events correctly pointing at the old one.
/// </summary>
public sealed class SqliteListenerStore
{
    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    public SqliteListenerStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<long> UpsertAsync(Protocol protocol, string bindAddress, int port, bool enabled, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(bindAddress);
        string name = $"{StorageFormat.Protocol(protocol)}:{bindAddress}:{port}";

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        await using (SqliteCommand upsert = connection.CreateCommand())
        {
            upsert.CommandText = """
                INSERT INTO listeners (name, protocol, bind_address, port, enabled, created_utc)
                VALUES ($name, $protocol, $bind, $port, $enabled, $created)
                ON CONFLICT(name) DO UPDATE SET
                    bind_address = excluded.bind_address,
                    port = excluded.port,
                    enabled = excluded.enabled;
                """;
            upsert.Parameters.AddWithValue("$name", name);
            upsert.Parameters.AddWithValue("$protocol", StorageFormat.Protocol(protocol));
            upsert.Parameters.AddWithValue("$bind", bindAddress);
            upsert.Parameters.AddWithValue("$port", port);
            upsert.Parameters.AddWithValue("$enabled", enabled ? 1 : 0);
            upsert.Parameters.AddWithValue("$created", Iso(_time.GetUtcNow()));
            await upsert.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
        }

        await using SqliteCommand select = connection.CreateCommand();
        select.CommandText = "SELECT listener_id FROM listeners WHERE name = $name;";
        select.Parameters.AddWithValue("$name", name);
        return (long)(await select.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false))!;
    }

    public async Task<IReadOnlyList<ListenerRecord>> ListAsync(CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT listener_id, name, protocol, bind_address, port, enabled, created_utc
            FROM listeners ORDER BY listener_id;
            """;

        var result = new List<ListenerRecord>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ListenerRecord(
                reader.GetInt64(0),
                reader.GetString(1),
                StorageFormat.ParseProtocol(reader.GetString(2)),
                reader.GetString(3),
                reader.GetInt32(4),
                reader.GetInt32(5) != 0,
                StorageFormat.ParseTimestamp(reader.GetString(6))));
        }

        return result;
    }

    private static string Iso(DateTimeOffset value) =>
        value.ToUniversalTime().UtcDateTime.ToString("O", CultureInfo.InvariantCulture);
}
