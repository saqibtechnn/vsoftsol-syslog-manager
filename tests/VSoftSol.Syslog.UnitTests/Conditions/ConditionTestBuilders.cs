using System.Text;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.UnitTests.Conditions;

internal static class ConditionTestBuilders
{
    public static ConditionComparison Cmp(string field, ConditionOperator op, string value = "") =>
        new() { Field = field, Operator = op, Value = value };

    public static ConditionGroup All(params ConditionNode[] children) =>
        new() { Join = ConditionJoin.And, Children = [.. children] };

    public static ConditionGroup Any(params ConditionNode[] children) =>
        new() { Join = ConditionJoin.Or, Children = [.. children] };

    public static SyslogEvent Event(
        string message = "login failed for user root",
        string sourceIp = "10.0.0.9",
        string? hostname = "core-sw-1",
        string? app = "sshd",
        Severity severity = Severity.Warning,
        Facility facility = Facility.Local7,
        string? vendor = "cisco-ios",
        Protocol protocol = Protocol.Udp,
        ParseStatus parseStatus = ParseStatus.Rfc3164,
        int occurrenceCount = 1,
        params EventField[] fields) => new()
        {
            ReceivedUtc = new DateTimeOffset(2026, 9, 8, 10, 0, 0, TimeSpan.Zero),
            SourceIp = sourceIp,
            Hostname = hostname,
            AppName = app,
            Severity = severity,
            Facility = facility,
            Protocol = protocol,
            Message = message,
            RawMessage = Encoding.UTF8.GetBytes(message),
            ParseStatus = parseStatus,
            OccurrenceCount = occurrenceCount,
            Vendor = vendor,
            Fields = fields,
        };
}
