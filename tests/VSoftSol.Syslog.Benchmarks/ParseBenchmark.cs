using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Engines;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Ingestion.Parsing;

namespace VSoftSol.Syslog.Benchmarks;

/// <summary>
/// PHASE_03 gate: parsing must not drop sustained ingest below 5,000 msg/sec. This
/// isolates the parse + vendor-extraction cost (no channel, no spill, no SQLite) over a
/// realistic mixed corpus. Three iterations, mean and standard deviation.
/// </summary>
[SimpleJob(RunStrategy.Throughput, warmupCount: 3, iterationCount: 5)]
[MemoryDiagnoser]
public class ParseBenchmark
{
    private const int Count = 20_000;

    private static readonly string[] Corpus =
    [
        "<190>123456: Mar  1 22:14:15.003 UTC: %LINK-3-UPDOWN: Interface GigabitEthernet0/1, changed state to down",
        "<187>123458: Mar  1 22:15:00.000 UTC: %SYS-5-CONFIG_I: Configured from console by admin on vty0",
        "<166>%ASA-6-302013: Built inbound TCP connection 12345 for outside:203.0.113.5/443 (203.0.113.5/443) to inside:10.0.0.5/51234 (10.0.0.5/51234)",
        "<189>date=2026-03-01 time=10:00:00 devname=\"fw1\" devid=\"FG100D\" logid=\"0000000013\" type=\"traffic\" subtype=\"forward\" srcip=10.0.0.5 dstip=8.8.8.8 dstport=53 action=\"accept\"",
        "<14>Mar  1 10:00:00 PA-VM 1,2026/03/01 10:00:00,001801000000,TRAFFIC,end,2560,2026/03/01 10:00:00,10.0.0.5,8.8.8.8,0.0.0.0,0.0.0.0,Allow-DNS,alice,,dns,vsys1,trust,untrust,e1/1,e1/2,LF,2026/03/01 10:00:00,12345,1,44321,53,0,0,0x0,udp,allow",
        "<38>Mar  1 10:00:00 router1 mgd[1234]: UI_CMDLINE_READ_LINE: User 'root', command 'show version'",
        "<13>Mar  1 10:00:00 MikroTik system,info,account user admin logged in from 10.0.0.5 via ssh",
        "<190>Mar  1 10:00:00 U6-Pro hostapd: ath0: STA aa:bb:cc:dd:ee:ff IEEE 802.11: authenticated",
        "<38>Mar  1 10:00:00 server1 sshd[1234]: Accepted publickey for admin from 10.0.0.5 port 51234 ssh2",
        "<34>1 2026-03-01T22:14:15.003Z mymachine.example.com su - ID47 [ex@1 k=\"v\"] su root failed for lonvick",
        "just some free text from an unknown appliance that will not parse",
        "<13>Oct 11 22:14:15 host randomd: a plain message with no vendor pattern",
    ];

    private RawFrame[] _frames = [];
    private MessageParser _parser = null!;

    [GlobalSetup]
    public void Setup()
    {
        _parser = ParsingComposition.Build().Parser;
        var received = new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero);
        _frames = new RawFrame[Count];
        for (int i = 0; i < Count; i++)
        {
            // Every message unique (like a real device stream), so no regex/string cache
            // masks the true per-message cost.
            string wire = Corpus[i % Corpus.Length].Replace("10.0.0.5", "10.0.0." + (i % 254), StringComparison.Ordinal)
                .Replace("Interface GigabitEthernet0/1", "Interface GigabitEthernet0/" + (i % 48), StringComparison.Ordinal);
            _frames[i] = new RawFrame(
                received, "203.0.113." + (i % 254 + 1), "udp:test", Protocol.Udp,
                System.Text.Encoding.UTF8.GetBytes(wire), truncated: false);
        }
    }

    [Benchmark(OperationsPerInvoke = Count)]
    public int ParseMixedCorpus()
    {
        int fields = 0;
        for (int i = 0; i < _frames.Length; i++)
        {
            SyslogEvent e = _parser.Parse(_frames[i]);
            fields += e.Fields.Count;
        }

        return fields;
    }
}
