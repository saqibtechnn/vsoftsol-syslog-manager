using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Search;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Search;
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
    private readonly SearchOptions _searchOptions;

    public ScopedEventReader(
        ILogRepository repository,
        SqliteConnectionFactory factory,
        IOptions<SearchOptions>? searchOptions = null)
    {
        _repository = repository ?? throw new ArgumentNullException(nameof(repository));
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _searchOptions = searchOptions?.Value ?? new SearchOptions();
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

    // ================================================================ Phase 5 — search

    /// <summary>
    /// Runs a Phase 5 search: parses <see cref="SearchRequest.QueryText"/>, applies the
    /// principal's scope, compiles to parameterised SQL, and returns a result page plus the
    /// (ceiling-capped) total count. An invalid query comes back as
    /// <see cref="SearchResult.Invalid"/>, never an exception.
    /// </summary>
    public async Task<SearchResult> SearchAsync(UserScope scope, SearchRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        SearchParseResult parsed = SearchQueryParser.Parse(request.QueryText);
        if (!parsed.Success)
        {
            return SearchResult.Invalid(parsed.Error!, parsed.ErrorPosition);
        }

        int limit = Math.Clamp(request.Limit, 0, _searchOptions.MaxPageSize);
        long offset = Math.Max(0, request.Offset);
        CompiledSearch compiled = new SearchCompiler().Compile(request with { Limit = limit }, parsed.Query!, scope);
        if (compiled.MatchesNothing)
        {
            return SearchResult.Success([], 0, totalIsLowerBound: false);
        }

        await using SqliteConnection readConnection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection fieldConnection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<SyslogEvent>(Math.Min(Math.Max(limit, 1), 1024));
        await using (SqliteCommand command = readConnection.CreateCommand())
        {
            command.CommandText =
                $"SELECT {EventRowMapper.Prefixed("e")} FROM events e WHERE {compiled.WhereSql} " +
                $"ORDER BY {compiled.OrderBySql} LIMIT $limit OFFSET $offset;";
            Bind(command, compiled.Parameters);
            command.Parameters.AddWithValue("$limit", limit);
            command.Parameters.AddWithValue("$offset", offset);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(EventRowMapper.Read(reader));
            }
        }

        await HydrateFieldsAsync(fieldConnection, rows, cancellationToken).ConfigureAwait(false);

        // If the page came back full there are at least `limit + offset` matches — skip the
        // second scan and report a lower bound. The exact count only runs when it is cheap
        // (a page that did not fill means the whole matching set is small).
        (long total, bool lowerBound) = rows.Count >= limit && limit > 0
            ? (offset + limit, true)
            : await CountAsync(fieldConnection, compiled, cancellationToken).ConfigureAwait(false);

        if (!lowerBound)
        {
            total = Math.Max(total, offset + rows.Count);
        }

        return SearchResult.Success(rows, total, lowerBound);
    }

    /// <summary>
    /// Streams every in-scope row matching the request, ordered by the request's sort, up
    /// to <see cref="SearchOptions.ExportMaxRows"/>. Backs export. An invalid query yields
    /// nothing. Fields are hydrated in pages on a second connection so memory stays flat.
    /// </summary>
    public async IAsyncEnumerable<SyslogEvent> SearchStreamAsync(
        UserScope scope, SearchRequest request, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        SearchParseResult parsed = SearchQueryParser.Parse(request.QueryText);
        if (!parsed.Success)
        {
            yield break;
        }

        CompiledSearch compiled = new SearchCompiler().Compile(request, parsed.Query!, scope);
        if (compiled.MatchesNothing)
        {
            yield break;
        }

        int cap = Math.Clamp(request.Limit <= 0 ? _searchOptions.ExportMaxRows : request.Limit, 1, _searchOptions.ExportMaxRows);

        await using SqliteConnection readConnection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection fieldConnection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = readConnection.CreateCommand();
        command.CommandText =
            $"SELECT {EventRowMapper.Prefixed("e")} FROM events e WHERE {compiled.WhereSql} " +
            $"ORDER BY {compiled.OrderBySql} LIMIT $cap;";
        Bind(command, compiled.Parameters);
        command.Parameters.AddWithValue("$cap", cap);

        var batch = new List<SyslogEvent>(512);
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            batch.Add(EventRowMapper.Read(reader));
            if (batch.Count == 512)
            {
                await HydrateFieldsAsync(fieldConnection, batch, cancellationToken).ConfigureAwait(false);
                foreach (SyslogEvent evt in batch)
                {
                    yield return evt;
                }

                batch.Clear();
            }
        }

        await HydrateFieldsAsync(fieldConnection, batch, cancellationToken).ConfigureAwait(false);
        foreach (SyslogEvent evt in batch)
        {
            yield return evt;
        }
    }

    /// <summary>
    /// Returns up to <see cref="SearchOptions.LiveTailBatchSize"/> in-scope rows matching
    /// the request whose <c>event_id</c> is greater than <paramref name="afterEventId"/>,
    /// oldest-first. Backs live tail. The request's upper time bound is ignored (tail
    /// always looks forward).
    /// </summary>
    public async Task<IReadOnlyList<SyslogEvent>> PollLiveAsync(
        UserScope scope, SearchRequest request, long afterEventId, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(request);

        SearchParseResult parsed = SearchQueryParser.Parse(request.QueryText);
        if (!parsed.Success)
        {
            return [];
        }

        SearchRequest forward = request with { ToUtc = DateTimeOffset.MaxValue };
        CompiledSearch compiled = new SearchCompiler().Compile(forward, parsed.Query!, scope);
        if (compiled.MatchesNothing)
        {
            return [];
        }

        await using SqliteConnection readConnection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection fieldConnection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);

        var rows = new List<SyslogEvent>();
        await using (SqliteCommand command = readConnection.CreateCommand())
        {
            command.CommandText =
                $"SELECT {EventRowMapper.Prefixed("e")} FROM events e " +
                $"WHERE {compiled.WhereSql} AND e.event_id > $after " +
                "ORDER BY e.event_id ASC LIMIT $batch;";
            Bind(command, compiled.Parameters);
            command.Parameters.AddWithValue("$after", afterEventId);
            command.Parameters.AddWithValue("$batch", _searchOptions.LiveTailBatchSize);

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                rows.Add(EventRowMapper.Read(reader));
            }
        }

        await HydrateFieldsAsync(fieldConnection, rows, cancellationToken).ConfigureAwait(false);
        return rows;
    }

    private static void Bind(SqliteCommand command, IReadOnlyList<SearchParameter> parameters)
    {
        foreach (SearchParameter p in parameters)
        {
            command.Parameters.AddWithValue(p.Name, p.Value);
        }
    }

    private async Task<(long Total, bool LowerBound)> CountAsync(
        SqliteConnection connection, CompiledSearch compiled, CancellationToken cancellationToken)
    {
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText =
            $"SELECT COUNT(*) FROM (SELECT 1 FROM events e WHERE {compiled.WhereSql} LIMIT $ceiling);";
        Bind(command, compiled.Parameters);
        command.Parameters.AddWithValue("$ceiling", _searchOptions.ExactCountCeiling + 1);

        long n = Convert.ToInt64(
            await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
            CultureInfo.InvariantCulture);

        return n > _searchOptions.ExactCountCeiling ? (_searchOptions.ExactCountCeiling, true) : (n, false);
    }

    private static async Task HydrateFieldsAsync(
        SqliteConnection connection, List<SyslogEvent> rows, CancellationToken cancellationToken)
    {
        if (rows.Count == 0)
        {
            return;
        }

        Dictionary<long, IReadOnlyList<EventField>> fields = await EventRowMapper
            .LoadFieldsAsync(connection, rows.Select(r => r.EventId).ToList(), cancellationToken)
            .ConfigureAwait(false);

        for (int i = 0; i < rows.Count; i++)
        {
            if (fields.TryGetValue(rows[i].EventId, out IReadOnlyList<EventField>? list) && list.Count > 0)
            {
                rows[i] = EventRowMapper.WithFields(rows[i], list);
            }
        }
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
