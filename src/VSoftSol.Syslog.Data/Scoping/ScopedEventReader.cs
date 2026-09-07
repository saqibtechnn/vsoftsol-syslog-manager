using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Scoping;

/// <summary>
/// The single chokepoint for reading events under a principal's <see cref="UserScope"/>
/// (PHASE_04 build item 4). Every UI data path — search, context view, exports, reports —
/// goes through here and never touches <see cref="ILogRepository"/> directly, so no future
/// page can bypass scope. Enforced by the architecture fitness test.
/// </summary>
/// <remarks>
/// Scope is applied by narrowing the <see cref="LogQuery"/> (stream membership and the
/// device-id set resolved from the visible device groups) before delegating to the
/// repository, and by an explicit per-event visibility check on the by-id paths. Both
/// mirror <see cref="UserScope.Allows"/>. A caller-supplied filter can only ever narrow
/// the result further — it cannot widen past the scope.
/// </remarks>
public sealed class ScopedEventReader
{
    private readonly ILogRepository _repository;
    private readonly SqliteConnectionFactory _factory;

    public ScopedEventReader(ILogRepository repository, SqliteConnectionFactory factory)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
    }

    public async Task<SyslogEvent?> GetByIdAsync(UserScope scope, long eventId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        SyslogEvent? evt = await _repository.GetByIdAsync(eventId, cancellationToken).ConfigureAwait(false);
        if (evt is null)
        {
            return null;
        }

        // Return null (not "forbidden") for an out-of-scope id so the by-id path is not an
        // existence oracle (IDOR).
        return await IsVisibleAsync(scope, eventId, cancellationToken).ConfigureAwait(false) ? evt : null;
    }

    public async IAsyncEnumerable<SyslogEvent> QueryAsync(
        UserScope scope, LogQuery query, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        (LogQuery scoped, bool impossible) = await ApplyScopeAsync(scope, query, cancellationToken).ConfigureAwait(false);
        if (impossible)
        {
            yield break;
        }

        await foreach (SyslogEvent evt in _repository.QueryAsync(scoped, cancellationToken).ConfigureAwait(false))
        {
            yield return evt;
        }
    }

    public async Task<long> CountAsync(UserScope scope, LogQuery query, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(query);

        (LogQuery scoped, bool impossible) = await ApplyScopeAsync(scope, query, cancellationToken).ConfigureAwait(false);
        return impossible ? 0 : await _repository.CountAsync(scoped, cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<SyslogEvent>> GetContextAsync(
        UserScope scope, long eventId, int before, int after, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);

        if (!await IsVisibleAsync(scope, eventId, cancellationToken).ConfigureAwait(false))
        {
            return [];
        }

        IReadOnlyList<SyslogEvent> context =
            await _repository.GetContextAsync(eventId, before, after, cancellationToken).ConfigureAwait(false);
        if (scope.IsUnrestricted)
        {
            return context;
        }

        var visible = new List<SyslogEvent>(context.Count);
        foreach (SyslogEvent evt in context)
        {
            if (await IsVisibleAsync(scope, evt.EventId, cancellationToken).ConfigureAwait(false))
            {
                visible.Add(evt);
            }
        }

        return visible;
    }

    private async Task<(LogQuery Scoped, bool Impossible)> ApplyScopeAsync(
        UserScope scope, LogQuery query, CancellationToken cancellationToken)
    {
        if (scope.IsUnrestricted)
        {
            return (query, false);
        }

        IReadOnlyList<long> streamIds = query.StreamIds;
        if (!scope.AllStreams)
        {
            streamIds = Intersect(query.StreamIds, scope.StreamIds);
            if (streamIds.Count == 0)
            {
                return (query, true);
            }
        }

        IReadOnlyList<long> deviceIds = query.DeviceIds;
        if (!scope.AllDeviceGroups)
        {
            IReadOnlyList<long> scopeDeviceIds =
                await ResolveDeviceIdsAsync(scope.DeviceGroupIds, cancellationToken).ConfigureAwait(false);
            if (scopeDeviceIds.Count == 0)
            {
                return (query, true);
            }

            deviceIds = Intersect(query.DeviceIds, scopeDeviceIds);
            if (deviceIds.Count == 0)
            {
                return (query, true);
            }
        }

        return (query with { StreamIds = streamIds, DeviceIds = deviceIds }, false);
    }

    private async Task<bool> IsVisibleAsync(UserScope scope, long eventId, CancellationToken cancellationToken)
    {
        if (scope.IsUnrestricted)
        {
            return true;
        }

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var eventStreamIds = new List<long>();
        if (!scope.AllStreams)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = "SELECT stream_id FROM event_streams WHERE event_id = $id;";
            command.Parameters.AddWithValue("$id", eventId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                eventStreamIds.Add(reader.GetInt64(0));
            }
        }

        var eventGroupIds = new List<long>();
        if (!scope.AllDeviceGroups)
        {
            await using SqliteCommand command = connection.CreateCommand();
            command.CommandText = """
                SELECT gm.group_id
                FROM events e
                JOIN device_group_members gm ON gm.device_id = e.device_id
                WHERE e.event_id = $id;
                """;
            command.Parameters.AddWithValue("$id", eventId);
            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                eventGroupIds.Add(reader.GetInt64(0));
            }
        }

        return scope.Allows(eventStreamIds, eventGroupIds);
    }

    private async Task<IReadOnlyList<long>> ResolveDeviceIdsAsync(
        IReadOnlyCollection<long> groupIds, CancellationToken cancellationToken)
    {
        if (groupIds.Count == 0)
        {
            return [];
        }

        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        var names = new List<string>(groupIds.Count);
        int i = 0;
        foreach (long groupId in groupIds)
        {
            string name = string.Create(CultureInfo.InvariantCulture, $"$g{i++}");
            command.Parameters.AddWithValue(name, groupId);
            names.Add(name);
        }

        command.CommandText =
            $"SELECT DISTINCT device_id FROM device_group_members WHERE group_id IN ({string.Join(", ", names)});";

        var result = new List<long>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(reader.GetInt64(0));
        }

        return result;
    }

    private static IReadOnlyList<long> Intersect(IReadOnlyList<long> callerIds, IReadOnlyCollection<long> scopeIds)
    {
        if (callerIds.Count == 0)
        {
            return [.. scopeIds];
        }

        var allowed = new HashSet<long>(scopeIds);
        return [.. callerIds.Where(allowed.Contains)];
    }
}
