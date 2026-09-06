using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.Ingestion.Parsing;

/// <summary>
/// RFC 5424 parser (PHASE_03 item 1). Header:
/// <c>&lt;PRI&gt;VERSION SP TIMESTAMP SP HOSTNAME SP APP-NAME SP PROCID SP MSGID SP SD SP MSG</c>.
/// Hand-written and allocation-light (no regex on the hot path); returns <c>false</c> for
/// anything that is not well-formed RFC 5424 so the RFC 3164 parser gets a turn.
/// </summary>
public sealed class Rfc5424Parser : ISyslogParser
{
    private const string Nil = "-";

    public bool TryParse(string text, DateTimeOffset receivedUtc, out SyslogParseResult result)
    {
        result = default!;
        if (string.IsNullOrEmpty(text) || text[0] != '<')
        {
            return false;
        }

        int close = text.IndexOf('>', StringComparison.Ordinal);
        if (close is < 2 or > 4)
        {
            return false;
        }

        if (!int.TryParse(text.AsSpan(1, close - 1), NumberStyles.None, CultureInfo.InvariantCulture, out int pri)
            || pri > SyslogPriority.MaxValue)
        {
            return false;
        }

        int pos = close + 1;

        // VERSION — RFC 5424 is version 1. Must be immediately after '>'.
        int versionEnd = pos;
        while (versionEnd < text.Length && char.IsAsciiDigit(text[versionEnd]))
        {
            versionEnd++;
        }

        if (versionEnd == pos || versionEnd - pos > 2
            || !int.TryParse(text.AsSpan(pos, versionEnd - pos), out int version) || version != 1
            || versionEnd >= text.Length || text[versionEnd] != ' ')
        {
            return false;
        }

        pos = versionEnd + 1;

        if (!ReadField(text, ref pos, out string timestamp)
            || !ReadField(text, ref pos, out string hostname)
            || !ReadField(text, ref pos, out string appName)
            || !ReadField(text, ref pos, out string procId)
            || !ReadField(text, ref pos, out string msgId))
        {
            return false;
        }

        DateTimeOffset? eventUtc = null;
        if (timestamp != Nil)
        {
            if (!TryParseTimestamp(timestamp, out DateTimeOffset parsed))
            {
                return false; // a present-but-unparseable timestamp means this is not clean 5424
            }

            eventUtc = parsed;
        }

        if (!TryReadStructuredData(text, ref pos, out string? sdJson))
        {
            return false;
        }

        string message = pos < text.Length ? text[pos..] : string.Empty;
        if (message.StartsWith('﻿'))
        {
            message = message[1..];
        }

        SyslogPriority priority = SyslogPriority.FromValue(pri);
        result = new SyslogParseResult
        {
            Status = ParseStatus.Rfc5424,
            Facility = priority.Facility,
            Severity = priority.Severity,
            EventUtc = eventUtc,
            Hostname = Normalise(hostname),
            AppName = Normalise(appName),
            ProcId = Normalise(procId),
            MsgId = Normalise(msgId),
            Message = message,
            StructuredDataJson = sdJson,
            FramingAnomaly = ContainsBareControl(message),
        };
        return true;
    }

    private static bool ReadField(string text, ref int pos, out string value)
    {
        if (pos >= text.Length)
        {
            value = string.Empty;
            return false;
        }

        int start = pos;
        while (pos < text.Length && text[pos] != ' ')
        {
            pos++;
        }

        if (pos >= text.Length)
        {
            value = string.Empty;
            return false; // header must be followed by at least a space + SD
        }

        value = text[start..pos];
        pos++; // consume the space
        return value.Length > 0;
    }

    private static bool TryReadStructuredData(string text, ref int pos, out string? json)
    {
        json = null;

        if (pos < text.Length && text[pos] == '-' && (pos + 1 >= text.Length || text[pos + 1] == ' '))
        {
            pos = Math.Min(text.Length, pos + 2);
            return true;
        }

        if (pos >= text.Length || text[pos] != '[')
        {
            return false;
        }

        using var elements = new PooledJson();
        while (pos < text.Length && text[pos] == '[')
        {
            pos++;
            int idStart = pos;
            while (pos < text.Length && text[pos] != ' ' && text[pos] != ']')
            {
                pos++;
            }

            if (pos >= text.Length)
            {
                return false;
            }

            string sdId = text[idStart..pos];
            elements.StartElement(sdId);

            while (pos < text.Length && text[pos] == ' ')
            {
                pos++;
                int nameStart = pos;
                while (pos < text.Length && text[pos] != '=')
                {
                    pos++;
                }

                if (pos + 1 >= text.Length || text[pos + 1] != '"')
                {
                    return false;
                }

                string name = text[nameStart..pos];
                pos += 2; // skip ="
                var sb = new StringBuilder();
                bool closed = false;
                while (pos < text.Length)
                {
                    char c = text[pos++];
                    if (c == '\\' && pos < text.Length)
                    {
                        sb.Append(text[pos++]);
                    }
                    else if (c == '"')
                    {
                        closed = true;
                        break;
                    }
                    else
                    {
                        sb.Append(c);
                    }
                }

                if (!closed)
                {
                    return false;
                }

                elements.AddParam(name, sb.ToString());
            }

            if (pos >= text.Length || text[pos] != ']')
            {
                return false;
            }

            pos++; // consume ']'
            elements.EndElement();
        }

        if (pos < text.Length && text[pos] == ' ')
        {
            pos++;
        }

        json = elements.Finish();
        return true;
    }

    private static bool TryParseTimestamp(string value, out DateTimeOffset result)
    {
        // RFC 5424 §6.2.3: RFC 3339 profile. DateTimeOffset round-trip handles the offset
        // and fractional seconds; force UTC.
        if (DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind | DateTimeStyles.AssumeUniversal, out DateTimeOffset dto))
        {
            result = dto.ToUniversalTime();
            return true;
        }

        result = default;
        return false;
    }

    private static string? Normalise(string value) => value == Nil || value.Length == 0 ? null : value;

    internal static bool ContainsBareControl(string s)
    {
        foreach (char c in s)
        {
            if (c is '\r' or '\n' or '\0')
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Builds the structured-data JSON with a pooled buffer and System.Text.Json.</summary>
    private sealed class PooledJson : IDisposable
    {
        private readonly ArrayBufferWriter<byte> _buffer = new();
        private readonly Utf8JsonWriter _writer;
        private bool _inElement;

        public PooledJson()
        {
            _writer = new Utf8JsonWriter(_buffer);
            _writer.WriteStartObject();
        }

        public void StartElement(string sdId)
        {
            _writer.WritePropertyName(sdId);
            _writer.WriteStartObject();
            _inElement = true;
        }

        public void AddParam(string name, string value) => _writer.WriteString(name, value);

        public void EndElement()
        {
            _writer.WriteEndObject();
            _inElement = false;
        }

        public string Finish()
        {
            if (_inElement)
            {
                _writer.WriteEndObject();
            }

            _writer.WriteEndObject();
            _writer.Flush();
            return Encoding.UTF8.GetString(_buffer.WrittenSpan);
        }

        public void Dispose() => _writer.Dispose();
    }
}
