using System.Text;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Search;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>
/// A deterministic, seeded corpus of events with devices, streams, and extracted fields —
/// stored in a real database and also held in memory so the golden-oracle evaluator can be
/// run against the same rows the SQL path sees.
/// </summary>
public sealed class SearchCorpus
{
    public static readonly string[] Words =
    [
        "failed", "password", "accepted", "session", "opened", "closed", "denied", "login",
        "logout", "user", "root", "admin", "invalid", "connection", "reset", "timeout",
        "error", "link", "interface", "changed", "state", "authentication", "sudo",
    ];

    public static readonly string[] Hosts = ["edge-fw-1", "core-sw-1", "core-sw-2", "dc-rtr-1", "wlc-1"];
    public static readonly string[] Apps = ["sshd", "kernel", "cron", "named", "systemd"];
    public static readonly string[] Vendors = ["cisco-ios", "linux", "fortigate", "juniper-junos"];
    public static readonly string[] Ips = ["10.0.0.9", "10.0.1.5", "10.0.1.6", "192.168.1.1", "198.51.100.7"];
    public static readonly string[] Users = ["root", "admin", "svc-backup", "operator", "guest"];
    public static readonly string[] Actions = ["allow", "deny", "drop", "reset"];
    public static readonly string[] StreamNames = ["All Events", "Firewall", "Auth", "Network", "Windows"];

    public DateTimeOffset WindowStart { get; } = new(2026, 8, 1, 0, 0, 0, TimeSpan.Zero);
    public DateTimeOffset WindowEnd { get; } = new(2026, 9, 8, 0, 0, 0, TimeSpan.Zero);

    public IReadOnlyList<CorpusEvent> Events { get; private set; } = [];

    public sealed record CorpusEvent(SyslogEvent Event, string? DeviceName, IReadOnlyList<string> Streams)
    {
        public QueryEvaluationContext Context => new() { DeviceName = DeviceName, StreamNames = Streams };
    }

    public static async Task<SearchCorpus> SeedAsync(SqliteTestDatabase db, int eventCount, int seed)
    {
        var corpus = new SearchCorpus();
        var rng = new Random(seed);

        // devices
        var deviceIds = new Dictionary<string, long>();
        foreach (string host in Hosts)
        {
            deviceIds[host] = await ExecScalarAsync(db,
                $"INSERT INTO devices (name, primary_ip, vendor, created_utc) VALUES ('{host}', NULL, NULL, 't'); SELECT last_insert_rowid();");
        }

        // streams (skip any the seeder already created)
        var streamIds = new Dictionary<string, long>();
        foreach (string name in StreamNames)
        {
            long existing = await ExecScalarOrZeroAsync(db, $"SELECT stream_id FROM streams WHERE name = '{name}';");
            streamIds[name] = existing != 0
                ? existing
                : await ExecScalarAsync(db,
                    $"INSERT INTO streams (name, created_utc) VALUES ('{name}', 't'); SELECT last_insert_rowid();");
        }

        var built = new List<CorpusEvent>(eventCount);
        long totalTicks = (corpus.WindowEnd - corpus.WindowStart).Ticks;

        for (int i = 0; i < eventCount; i++)
        {
            int wordCount = rng.Next(3, 9);
            var msgWords = new string[wordCount];
            for (int w = 0; w < wordCount; w++)
            {
                msgWords[w] = Words[rng.Next(Words.Length)];
            }

            string message = string.Join(' ', msgWords);
            string host = Hosts[rng.Next(Hosts.Length)];
            bool hasDevice = rng.Next(100) < 80;
            var severity = (Severity)rng.Next(0, 8);
            var facility = (Facility)rng.Next(0, 24);
            string app = Apps[rng.Next(Apps.Length)];
            string vendor = Vendors[rng.Next(Vendors.Length)];
            string ip = Ips[rng.Next(Ips.Length)];
            var received = corpus.WindowStart.AddTicks((long)(rng.NextDouble() * totalTicks));
            DateTimeOffset? eventUtc = rng.Next(10) < 8 ? received.AddSeconds(-rng.Next(0, 120)) : null;
            var parseStatus = (ParseStatus)rng.Next(0, 3);
            var protocol = (Protocol)rng.Next(0, 3);

            var fields = new List<EventField>
            {
                new("srcport", rng.Next(1, 65535).ToString(System.Globalization.CultureInfo.InvariantCulture)),
                new("user", Users[rng.Next(Users.Length)]),
                new("action", Actions[rng.Next(Actions.Length)]),
                new("bytes", (rng.Next(0, 20) * 1000).ToString(System.Globalization.CultureInfo.InvariantCulture)),
            };

            var evt = new SyslogEvent
            {
                ReceivedUtc = received,
                EventUtc = eventUtc,
                SourceIp = ip,
                Hostname = rng.Next(10) < 9 ? host : null,
                AppName = app,
                ProcId = rng.Next(2) == 0 ? rng.Next(100, 9999).ToString(System.Globalization.CultureInfo.InvariantCulture) : null,
                Facility = facility,
                Severity = severity,
                Protocol = protocol,
                Message = message,
                RawMessage = Encoding.UTF8.GetBytes(message),
                ParseStatus = parseStatus,
                DeviceId = hasDevice ? deviceIds[host] : null,
                Vendor = vendor,
                Fields = fields,
            };

            long id = await db.Repository.AppendAsync(evt, CancellationToken.None);

            var streams = new List<string> { "All Events" };
            foreach (string s in new[] { "Firewall", "Auth", "Network", "Windows" })
            {
                if (rng.Next(100) < 35)
                {
                    streams.Add(s);
                }
            }

            foreach (string s in streams)
            {
                await ExecAsync(db, $"INSERT OR IGNORE INTO event_streams (event_id, stream_id) VALUES ({id}, {streamIds[s]});");
            }

            var stored = CloneWithId(evt, id);
            built.Add(new CorpusEvent(stored, hasDevice ? host : null, streams));
        }

        await db.SyncSearchAsync();
        corpus.Events = built;
        return corpus;
    }

    private static SyslogEvent CloneWithId(SyslogEvent e, long id) => new()
    {
        EventId = id,
        ReceivedUtc = e.ReceivedUtc,
        EventUtc = e.EventUtc,
        SourceIp = e.SourceIp,
        Hostname = e.Hostname,
        AppName = e.AppName,
        ProcId = e.ProcId,
        MsgId = e.MsgId,
        Facility = e.Facility,
        Severity = e.Severity,
        Protocol = e.Protocol,
        ListenerId = e.ListenerId,
        Message = e.Message,
        RawMessage = e.RawMessage,
        ParseStatus = e.ParseStatus,
        OccurrenceCount = e.OccurrenceCount,
        StructuredDataJson = e.StructuredDataJson,
        DeviceId = e.DeviceId,
        Vendor = e.Vendor,
        Fields = e.Fields,
    };

    private static async Task ExecAsync(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        await command.ExecuteNonQueryAsync();
    }

    private static async Task<long> ExecScalarAsync(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(), System.Globalization.CultureInfo.InvariantCulture);
    }

    private static async Task<long> ExecScalarOrZeroAsync(SqliteTestDatabase db, string sql)
    {
        await using SqliteConnection connection = await db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        object? result = await command.ExecuteScalarAsync();
        return result is null || result is DBNull ? 0 : Convert.ToInt64(result, System.Globalization.CultureInfo.InvariantCulture);
    }
}
