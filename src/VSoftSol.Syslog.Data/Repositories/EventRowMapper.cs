using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Data.Repositories;

/// <summary>
/// Reads <c>events</c> rows into <see cref="SyslogEvent"/> and hydrates their
/// <c>event_fields</c>. Shared by <see cref="SqliteLogRepository"/> and the Phase 5 search
/// executor (<c>ScopedEventReader</c>) so both project the canonical schema identically.
/// </summary>
internal static class EventRowMapper
{
    /// <summary>The event column list, in the order <see cref="Read"/> expects. Alias-free.</summary>
    public const string Columns =
        "event_id, received_utc, event_utc, source_ip, hostname, app_name, proc_id, msg_id, " +
        "facility, severity, protocol, listener_id, message, raw_message, parse_status, " +
        "occurrence_count, structured_data_json, device_id, vendor";

    /// <summary>The same list, each column prefixed with <paramref name="alias"/>.</summary>
    public static string Prefixed(string alias) =>
        string.Join(", ", Columns.Split(',', StringSplitOptions.TrimEntries).Select(c => $"{alias}.{c}"));

    public static SyslogEvent Read(SqliteDataReader reader)
    {
        long eventId = reader.GetInt64(0);
        DateTimeOffset received = StorageFormat.ParseTimestamp(reader.GetString(1));
        DateTimeOffset? eventUtc = reader.IsDBNull(2) ? null : StorageFormat.ParseTimestamp(reader.GetString(2));
        string sourceIp = reader.GetString(3);
        string? hostname = reader.IsDBNull(4) ? null : reader.GetString(4);
        string? appName = reader.IsDBNull(5) ? null : reader.GetString(5);
        string? procId = reader.IsDBNull(6) ? null : reader.GetString(6);
        string? msgId = reader.IsDBNull(7) ? null : reader.GetString(7);
        var facility = (Facility)reader.GetInt32(8);
        var severity = (Severity)reader.GetInt32(9);
        Protocol protocol = StorageFormat.ParseProtocol(reader.GetString(10));
        long listenerId = reader.IsDBNull(11) ? 0 : reader.GetInt64(11);
        string message = reader.GetString(12);
        byte[] raw = (byte[])reader[13];
        ParseStatus parseStatus = StorageFormat.ParseParseStatus(reader.GetString(14));
        int occurrence = reader.GetInt32(15);
        string? structured = reader.IsDBNull(16) ? null : reader.GetString(16);
        long? deviceId = reader.IsDBNull(17) ? null : reader.GetInt64(17);
        string? vendor = reader.IsDBNull(18) ? null : reader.GetString(18);

        return new SyslogEvent
        {
            EventId = eventId,
            ReceivedUtc = received,
            EventUtc = eventUtc,
            SourceIp = sourceIp,
            Hostname = hostname,
            AppName = appName,
            ProcId = procId,
            MsgId = msgId,
            Facility = facility,
            Severity = severity,
            Protocol = protocol,
            ListenerId = listenerId,
            Message = message,
            RawMessage = raw,
            ParseStatus = parseStatus,
            OccurrenceCount = occurrence,
            StructuredDataJson = structured,
            DeviceId = deviceId,
            Vendor = vendor,
        };
    }

    public static SyslogEvent WithFields(SyslogEvent evt, IReadOnlyList<EventField> fields) => new()
    {
        EventId = evt.EventId,
        ReceivedUtc = evt.ReceivedUtc,
        EventUtc = evt.EventUtc,
        SourceIp = evt.SourceIp,
        Hostname = evt.Hostname,
        AppName = evt.AppName,
        ProcId = evt.ProcId,
        MsgId = evt.MsgId,
        Facility = evt.Facility,
        Severity = evt.Severity,
        Protocol = evt.Protocol,
        ListenerId = evt.ListenerId,
        Message = evt.Message,
        RawMessage = evt.RawMessage,
        ParseStatus = evt.ParseStatus,
        OccurrenceCount = evt.OccurrenceCount,
        StructuredDataJson = evt.StructuredDataJson,
        DeviceId = evt.DeviceId,
        Vendor = evt.Vendor,
        Fields = fields,
    };

    public static async Task<Dictionary<long, IReadOnlyList<EventField>>> LoadFieldsAsync(
        SqliteConnection connection,
        IReadOnlyCollection<long> eventIds,
        CancellationToken cancellationToken)
    {
        var map = new Dictionary<long, IReadOnlyList<EventField>>();
        if (eventIds.Count == 0)
        {
            return map;
        }

        await using SqliteCommand command = connection.CreateCommand();
        var names = new List<string>(eventIds.Count);
        int i = 0;
        foreach (long id in eventIds)
        {
            string name = $"$f{i++}";
            command.Parameters.AddWithValue(name, id);
            names.Add(name);
        }

        command.CommandText =
            $"SELECT event_id, name, value FROM event_fields WHERE event_id IN ({string.Join(", ", names)}) " +
            "ORDER BY event_id, rowid;";

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            long id = reader.GetInt64(0);
            if (!map.TryGetValue(id, out IReadOnlyList<EventField>? bucket))
            {
                bucket = new List<EventField>();
                map[id] = bucket;
            }

            ((List<EventField>)bucket).Add(new EventField(reader.GetString(1), reader.GetString(2)));
        }

        return map;
    }
}
