using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Ingestion;
using VSoftSol.Syslog.Ingestion.Parsing;
using Xunit;
using Xunit.Abstractions;

namespace VSoftSol.Syslog.UnitTests.Parsing;

/// <summary>
/// PHASE_03 differential / oracle testing. The reference implementation here is an
/// <b>independent</b> regex-based parser (<see cref="RegexReferenceParser"/>) written to a
/// different strategy than the production hand-written span parser. It runs the full
/// fixture corpus and compares the RFC-header fields. Docker/WSL are not available on this
/// build host, so this substitutes for a live rsyslog/syslog-ng run; the divergence rule
/// still holds — every mismatch is either a bug or a documented deliberate difference.
/// </summary>
[Trait("Category", "Parsing")]
public sealed class OracleDifferentialTests(ITestOutputHelper output)
{
    private static readonly MessageParser Production = ParsingComposition.Build().Parser;

    [Fact]
    public void ProductionParser_AgreesWithTheIndependentReference_OnEveryFixture()
    {
        var divergences = new List<string>();
        int compared = 0;

        foreach (string file in FixtureFiles())
        {
            foreach (string line in File.ReadAllLines(file).Where(l => l.Trim().Length > 0))
            {
                string wire = JsonDocument.Parse(line).RootElement.GetProperty("wire").GetString()!;
                byte[] bytes = Encoding.UTF8.GetBytes(wire);

                SyslogEvent prod = Production.Parse(new RawFrame(
                    new DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero), "203.0.113.200", "udp:test",
                    Protocol.Udp, bytes, false));

                RegexReferenceParser.Result? oracle = RegexReferenceParser.Parse(wire);
                if (oracle is null)
                {
                    // The reference could not parse it — production may still (that is fine,
                    // production is more lenient); nothing to compare.
                    continue;
                }

                compared++;
                Compare(wire, prod, oracle.Value, divergences);
            }
        }

        output.WriteLine($"compared {compared} fixtures against the independent reference");
        divergences.Should().BeEmpty(
            "every divergence must be a bug or a documented deliberate difference:\n" + string.Join("\n", divergences));
    }

    private static void Compare(string wire, SyslogEvent prod, RegexReferenceParser.Result oracle, List<string> divergences)
    {
        void Check(string field, object? a, object? b)
        {
            if (!Equals(a, b))
            {
                divergences.Add($"[{field}] production='{a}' reference='{b}'  <=  {Trunc(wire)}");
            }
        }

        Check("facility", (int)prod.Facility, (int)oracle.Facility);
        Check("severity", (int)prod.Severity, (int)oracle.Severity);
        Check("hostname", prod.Hostname, oracle.Hostname);
        Check("app_name", prod.AppName, oracle.AppName);
        Check("proc_id", prod.ProcId, oracle.ProcId);

        if (oracle.MsgId is not null)
        {
            Check("msg_id", prod.MsgId, oracle.MsgId);
        }

        if (oracle.Message is not null)
        {
            Check("message", prod.Message, oracle.Message);
        }
    }

    private static string Trunc(string s) => s.Length <= 90 ? s : s[..90] + "…";

    private static IEnumerable<string> FixtureFiles()
    {
        string root = Path.Combine(AppContext.BaseDirectory, "fixtures", "messages");
        return Directory.EnumerateFiles(root, "corpus.jsonl", SearchOption.AllDirectories);
    }

    /// <summary>An independent, regex-driven RFC 3164 / 5424 parser used only as the oracle.</summary>
    private static class RegexReferenceParser
    {
        private static readonly Regex Rfc5424 = new(
            @"^<(?<pri>\d{1,3})>1 (?<ts>\S+) (?<host>\S+) (?<app>\S+) (?<procid>\S+) (?<msgid>\S+) (?<rest>.*)$",
            RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

        private static readonly Regex Rfc3164 = new(
            @"^<(?<pri>\d{1,3})>(?:(?<seq>\d+): )?(?<mon>Jan|Feb|Mar|Apr|May|Jun|Jul|Aug|Sep|Oct|Nov|Dec)\s+" +
            @"(?<day>\d{1,2})\s+(?:(?<year>\d{4})\s+)?(?<time>\d{2}:\d{2}:\d{2}(?:\.\d+)?)(?::)?(?:\s+[A-Z]{2,5}:)?\s+(?<rest>.*)$",
            RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

        private static readonly Regex HostTag = new(
            @"^(?<host>[^\s%][^\s]*)\s+(?<app>[A-Za-z][\w./-]*)(?:\[(?<pid>[^\]]+)\])?:\s+(?<msg>.*)$",
            RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromSeconds(1));

        public readonly record struct Result(
            Facility Facility, Severity Severity, string? Hostname, string? AppName, string? ProcId, string? MsgId, string? Message);

        public static Result? Parse(string text)
        {
            Match m5 = Rfc5424.Match(text);
            if (m5.Success && int.TryParse(m5.Groups["pri"].Value, out int pri5) && pri5 <= 191)
            {
                SyslogPriority p = SyslogPriority.FromValue(pri5);
                string rest = m5.Groups["rest"].Value;
                rest = StripSd(rest).TrimStart('﻿');
                return new Result(
                    p.Facility, p.Severity,
                    Nil(m5.Groups["host"].Value), Nil(m5.Groups["app"].Value),
                    Nil(m5.Groups["procid"].Value), Nil(m5.Groups["msgid"].Value), rest);
            }

            Match m3 = Rfc3164.Match(text);
            if (m3.Success && int.TryParse(m3.Groups["pri"].Value, out int pri3) && pri3 <= 191)
            {
                SyslogPriority p = SyslogPriority.FromValue(pri3);
                string rest = m3.Groups["rest"].Value;

                if (rest.StartsWith('%'))
                {
                    return new Result(p.Facility, p.Severity, null, null, null, null, rest);
                }

                Match ht = HostTag.Match(rest);
                if (ht.Success)
                {
                    return new Result(
                        p.Facility, p.Severity, ht.Groups["host"].Value, ht.Groups["app"].Value,
                        ht.Groups["pid"].Success ? ht.Groups["pid"].Value : null, null, ht.Groups["msg"].Value);
                }

                // Host but no recognisable tag.
                int sp = rest.IndexOf(' ', StringComparison.Ordinal);
                return sp > 0 && !rest[..sp].Contains(',', StringComparison.Ordinal) && !rest[..sp].Contains('=', StringComparison.Ordinal)
                    ? new Result(p.Facility, p.Severity, rest[..sp], null, null, null, null)
                    : new Result(p.Facility, p.Severity, null, null, null, null, null);
            }

            return null;
        }

        private static string StripSd(string rest)
        {
            if (rest.StartsWith("- ", StringComparison.Ordinal))
            {
                return rest[2..];
            }

            if (rest.StartsWith('['))
            {
                int depth = 0;
                for (int i = 0; i < rest.Length; i++)
                {
                    if (rest[i] == '[')
                    {
                        depth++;
                    }
                    else if (rest[i] == ']')
                    {
                        depth--;
                        if (depth == 0 && i + 1 < rest.Length && rest[i + 1] == ' ')
                        {
                            return rest[(i + 2)..];
                        }
                    }
                }
            }

            return rest;
        }

        private static string? Nil(string v) => v is "-" or "" ? null : v;

        private static string ToInvariant(double d) => d.ToString(CultureInfo.InvariantCulture);
    }
}
