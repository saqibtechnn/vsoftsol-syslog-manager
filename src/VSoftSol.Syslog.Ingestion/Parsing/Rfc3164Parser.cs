using System.Globalization;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Ingestion.Parsing;

/// <summary>
/// RFC 3164 (BSD) parser (PHASE_03 item 2): <c>&lt;PRI&gt;Mmm d HH:mm:ss HOST TAG[pid]: MSG</c>.
/// The BSD timestamp has no year and no timezone, so the year is inferred from
/// <c>received_utc</c> (handling the 31 Dec / 1 Jan rollover both ways) and the result is
/// flagged <see cref="SyslogParseResult.TimestampAmbiguous"/>. Deliberately lenient — this
/// is the last stage before <c>raw</c> — but it still returns <c>false</c> when it cannot
/// find a PRI or a BSD timestamp, so genuinely unstructured input falls through to raw.
/// </summary>
public sealed class Rfc3164Parser : ISyslogParser
{
    private static readonly string[] Months =
        ["Jan", "Feb", "Mar", "Apr", "May", "Jun", "Jul", "Aug", "Sep", "Oct", "Nov", "Dec"];

    public bool TryParse(string text, DateTimeOffset receivedUtc, out SyslogParseResult result)
    {
        result = default!;
        if (string.IsNullOrEmpty(text))
        {
            return false;
        }

        ReadOnlySpan<char> span = text;
        int pos = 0;

        SyslogPriority priority = new(Facility.User, Severity.Notice);
        bool hadPri = false;
        if (span[0] == '<')
        {
            int close = text.IndexOf('>', StringComparison.Ordinal);
            if (close is >= 2 and <= 4
                && int.TryParse(span[1..close], NumberStyles.None, CultureInfo.InvariantCulture, out int pri)
                && pri <= SyslogPriority.MaxValue)
            {
                priority = SyslogPriority.FromValue(pri);
                hadPri = true;
                pos = close + 1;
            }
        }

        // Optional Cisco IOS sequence-number prefix: "123456: "
        int seqSave = pos;
        while (pos < span.Length && char.IsAsciiDigit(span[pos]))
        {
            pos++;
        }

        if (pos > seqSave && pos + 1 < span.Length && span[pos] == ':' && span[pos + 1] == ' ')
        {
            pos += 2;
        }
        else
        {
            pos = seqSave;
        }

        SkipSpaces(span, ref pos);

        bool hadTimestamp = TryReadBsdTimestamp(span, ref pos, receivedUtc, out DateTimeOffset? eventUtc);

        if (!hadPri && !hadTimestamp)
        {
            return false;
        }

        // Cisco puts a second ": " after the timestamp before "%FACILITY-...".
        if (pos < span.Length && span[pos] == ':')
        {
            pos++;
        }

        SkipSpaces(span, ref pos);

        // Optional timezone token some devices emit with "show-timezone" (e.g. "UTC:", "PST").
        if (hadTimestamp)
        {
            int tzStart = pos;
            int tzEnd = tzStart;
            while (tzEnd < span.Length && char.IsAsciiLetter(span[tzEnd]))
            {
                tzEnd++;
            }

            // Require the trailing ':' so we do not eat a short hostname (Cisco emits "UTC:").
            if (tzEnd - tzStart is >= 2 and <= 5 && tzEnd < span.Length && span[tzEnd] == ':')
            {
                pos = tzEnd + 1;
                SkipSpaces(span, ref pos);
            }
        }

        string? hostname = null;
        string? tag = null;
        string? procId = null;

        // HOSTNAME then TAG. Both optional in the wild. A token that ends with ':' or '[' is
        // the tag; otherwise the first token is the host and the next is the tag. A token
        // starting with '%' is Cisco's mnemonic and a "key=value" token is FortiGate-style
        // KV — in both cases there is no host and no tag, straight to the message.
        int firstStart = pos;
        int firstEnd = TokenEnd(span, pos);
        if (firstEnd > firstStart && span[firstStart] != '%' && !IsKeyValueStart(span[firstStart..firstEnd]))
        {
            ReadOnlySpan<char> first = span[firstStart..firstEnd];
            if (LooksLikeTagStart(first))
            {
                (tag, procId) = SplitTag(first, out int consumed);
                pos = firstStart + consumed;
            }
            else
            {
                hostname = first.ToString();
                pos = firstEnd;
                SkipSpaces(span, ref pos);
                int secondStart = pos;
                int secondEnd = TokenEnd(span, pos);
                if (secondEnd > secondStart && LooksLikeTagStart(span[secondStart..secondEnd]))
                {
                    (tag, procId) = SplitTag(span[secondStart..secondEnd], out int consumed);
                    pos = secondStart + consumed;
                }
            }
        }

        if (pos < span.Length && span[pos] == ':')
        {
            pos++;
        }

        SkipSpaces(span, ref pos);
        string message = pos < span.Length ? span[pos..].ToString() : string.Empty;

        result = new SyslogParseResult
        {
            Status = ParseStatus.Rfc3164,
            Facility = priority.Facility,
            Severity = priority.Severity,
            EventUtc = eventUtc,
            TimestampAmbiguous = hadTimestamp,
            Hostname = hostname,
            AppName = tag,
            ProcId = procId,
            Message = message,
            FramingAnomaly = Rfc5424Parser.ContainsBareControl(message),
        };
        return true;
    }

    private static bool TryReadBsdTimestamp(
        ReadOnlySpan<char> span, ref int pos, DateTimeOffset receivedUtc, out DateTimeOffset? eventUtc)
    {
        eventUtc = null;
        int start = pos;
        if (pos + 3 > span.Length)
        {
            return false;
        }

        int month = -1;
        for (int mi = 0; mi < Months.Length; mi++)
        {
            if (span[pos..].StartsWith(Months[mi], StringComparison.OrdinalIgnoreCase))
            {
                month = mi;
                break;
            }
        }

        if (month < 0)
        {
            return false;
        }

        pos += 3;
        SkipSpaces(span, ref pos);

        int dayStart = pos;
        while (pos < span.Length && char.IsAsciiDigit(span[pos]))
        {
            pos++;
        }

        if (pos == dayStart || !int.TryParse(span[dayStart..pos], out int day) || day is < 1 or > 31)
        {
            pos = start;
            return false;
        }

        SkipSpaces(span, ref pos);

        // Optional 4-digit year some devices insert (Cisco ASA "logging timestamp"): "Mmm dd yyyy HH:MM:SS".
        int explicitYear = 0;
        int yearStart = pos;
        while (pos < span.Length && char.IsAsciiDigit(span[pos]))
        {
            pos++;
        }

        if (pos - yearStart == 4 && int.TryParse(span[yearStart..pos], out int y) && y is >= 1970 and <= 2100)
        {
            explicitYear = y;
            SkipSpaces(span, ref pos);
        }
        else
        {
            pos = yearStart; // that run of digits was the hour, not a year
        }

        // HH:mm:ss(.fff)?
        int timeStart = pos;
        while (pos < span.Length && (char.IsAsciiDigit(span[pos]) || span[pos] is ':' or '.'))
        {
            pos++;
        }

        // A trailing ':' (Cisco puts "15.003: %SYS-...") is not part of the time.
        int timeEnd = pos;
        while (timeEnd > timeStart && !char.IsAsciiDigit(span[timeEnd - 1]))
        {
            timeEnd--;
        }

        ReadOnlySpan<char> time = span[timeStart..timeEnd];
        if (!TryParseTime(time, out int hh, out int mm, out int ss, out int frac))
        {
            pos = start;
            return false;
        }

        int year = explicitYear > 0 ? explicitYear : InferYear(month + 1, day, receivedUtc);
        try
        {
            var local = new DateTime(year, month + 1, day, hh, mm, ss, DateTimeKind.Unspecified).AddTicks(frac);
            // No timezone in the message: treat the wall-clock as UTC and flag it ambiguous.
            eventUtc = new DateTimeOffset(local, TimeSpan.Zero);
            return true;
        }
        catch (ArgumentOutOfRangeException)
        {
            pos = start;
            return false;
        }
    }

    private static bool TryParseTime(ReadOnlySpan<char> time, out int hh, out int mm, out int ss, out int fracTicks)
    {
        hh = mm = ss = fracTicks = 0;
        int firstColon = time.IndexOf(':');
        if (firstColon < 0)
        {
            return false;
        }

        int secondColon = time[(firstColon + 1)..].IndexOf(':');
        if (secondColon < 0)
        {
            return false;
        }

        secondColon += firstColon + 1;

        if (!int.TryParse(time[..firstColon], out hh) || hh is < 0 or > 23)
        {
            return false;
        }

        if (!int.TryParse(time[(firstColon + 1)..secondColon], out mm) || mm is < 0 or > 59)
        {
            return false;
        }

        ReadOnlySpan<char> secPart = time[(secondColon + 1)..];
        int dot = secPart.IndexOf('.');
        ReadOnlySpan<char> whole = dot < 0 ? secPart : secPart[..dot];
        if (!int.TryParse(whole, out ss) || ss is < 0 or > 59)
        {
            return false;
        }

        if (dot >= 0)
        {
            ReadOnlySpan<char> fracDigits = secPart[(dot + 1)..];
            if (fracDigits.Length > 0 && int.TryParse(fracDigits[..Math.Min(7, fracDigits.Length)], out int f))
            {
                for (int i = fracDigits.Length; i < 7; i++)
                {
                    f *= 10;
                }

                fracTicks = f;
            }
        }

        return true;
    }

    private static int InferYear(int month, int day, DateTimeOffset receivedUtc)
    {
        int year = receivedUtc.Year;
        var candidate = SafeDate(year, month, day);
        var received = receivedUtc.Date;

        // Message dated well in the future of receipt → it is from last year (Dec seen in Jan).
        if (candidate - received > TimeSpan.FromDays(2))
        {
            return year - 1;
        }

        // Message dated well in the past of receipt near a year boundary → next year (Jan seen in Dec).
        if (received - candidate > TimeSpan.FromDays(300))
        {
            return year + 1;
        }

        return year;
    }

    private static DateTime SafeDate(int year, int month, int day)
    {
        try
        {
            return new DateTime(year, month, Math.Min(day, DateTime.DaysInMonth(year, month)), 0, 0, 0, DateTimeKind.Utc);
        }
        catch (ArgumentOutOfRangeException)
        {
            return new DateTime(year, month, 1, 0, 0, 0, DateTimeKind.Utc);
        }
    }

    private static void SkipSpaces(ReadOnlySpan<char> span, ref int pos)
    {
        while (pos < span.Length && span[pos] == ' ')
        {
            pos++;
        }
    }

    private static int TokenEnd(ReadOnlySpan<char> span, int pos)
    {
        while (pos < span.Length && span[pos] != ' ')
        {
            pos++;
        }

        return pos;
    }

    private static bool IsKeyValueStart(ReadOnlySpan<char> token)
    {
        int eq = token.IndexOf('=');
        if (eq <= 0)
        {
            return false;
        }

        foreach (char c in token[..eq])
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-' or '.'))
            {
                return false;
            }
        }

        return true;
    }

    private static bool LooksLikeTagStart(ReadOnlySpan<char> token)
    {
        // Cisco's "%FACILITY-SEVERITY-MNEMONIC:" is not a BSD tag — the vendor pack extracts
        // it and it stays in the message. A comma means CSV (Palo Alto), never a tag.
        if (token.Length == 0 || token[0] == '%' || token.Contains(','))
        {
            return false;
        }

        int end = token.IndexOfAny(':', '[');
        ReadOnlySpan<char> name = end < 0 ? token : token[..end];

        // A real BSD tag is a program name: starts with a letter, then word chars / . - /.
        if (name.Length == 0 || !char.IsAsciiLetter(name[0]))
        {
            return false;
        }

        foreach (char c in name)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.' or '-' or '/'))
            {
                return false;
            }
        }

        return end >= 0 || name[^1] == ':';
    }

    private static (string Tag, string? ProcId) SplitTag(ReadOnlySpan<char> token, out int consumed)
    {
        int bracket = token.IndexOf('[');
        int colon = token.IndexOf(':');

        if (bracket >= 0 && (colon < 0 || bracket < colon))
        {
            int rb = token[bracket..].IndexOf(']');
            string tag = token[..bracket].ToString();
            string? pid = rb > 1 ? token.Slice(bracket + 1, rb - 1).ToString() : null;
            consumed = rb >= 0 ? bracket + rb + 1 : token.Length;
            if (consumed < token.Length && token[consumed] == ':')
            {
                consumed++;
            }

            return (tag, pid);
        }

        if (colon >= 0)
        {
            consumed = colon + 1;
            return (token[..colon].ToString(), null);
        }

        consumed = token.Length;
        return (token.ToString().TrimEnd(':'), null);
    }
}
