using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Search;

/// <summary>
/// Supplies the values the search filter sidebar offers: the devices and streams a
/// principal is allowed to filter by. Scope is honoured here too — a stream-restricted
/// user is not shown the names of streams they cannot see.
/// </summary>
public sealed class SqliteSearchFacets(SqliteConnectionFactory factory)
{
    private readonly SqliteConnectionFactory _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    public sealed record FacetValue(long Id, string Name, string? Extra = null);

    public async Task<IReadOnlyList<FacetValue>> ListDevicesAsync(UserScope scope, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        if (scope.AllDeviceGroups)
        {
            command.CommandText = "SELECT device_id, name, primary_ip FROM devices ORDER BY name COLLATE NOCASE;";
        }
        else
        {
            var names = BindIds(command, scope.DeviceGroupIds, "g");
            command.CommandText =
                "SELECT d.device_id, d.name, d.primary_ip FROM devices d " +
                $"WHERE d.device_id IN (SELECT device_id FROM device_group_members WHERE group_id IN ({names})) " +
                "ORDER BY d.name COLLATE NOCASE;";
        }

        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<FacetValue>> ListStreamsAsync(UserScope scope, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();

        if (scope.AllStreams)
        {
            command.CommandText = "SELECT stream_id, name, NULL FROM streams ORDER BY sort_order, name COLLATE NOCASE;";
        }
        else
        {
            var names = BindIds(command, scope.StreamIds, "s");
            command.CommandText =
                $"SELECT stream_id, name, NULL FROM streams WHERE stream_id IN ({names}) " +
                "ORDER BY sort_order, name COLLATE NOCASE;";
        }

        return await ReadAsync(command, cancellationToken).ConfigureAwait(false);
    }

    private static string BindIds(SqliteCommand command, IReadOnlyCollection<long> ids, string prefix)
    {
        var names = new List<string>();
        int i = 0;
        foreach (long id in ids)
        {
            string name = $"${prefix}{i++}";
            command.Parameters.AddWithValue(name, id);
            names.Add(name);
        }

        return names.Count == 0 ? "NULL" : string.Join(", ", names);
    }

    private static async Task<IReadOnlyList<FacetValue>> ReadAsync(SqliteCommand command, CancellationToken cancellationToken)
    {
        var result = new List<FacetValue>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new FacetValue(
                reader.GetInt64(0),
                reader.GetString(1),
                reader.IsDBNull(2) ? null : reader.GetString(2)));
        }

        return result;
    }
}
