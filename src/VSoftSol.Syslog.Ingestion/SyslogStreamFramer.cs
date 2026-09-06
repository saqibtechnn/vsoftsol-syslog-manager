namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Per-connection TCP framing state machine (PHASE_02 item 2). Auto-detects, from the
/// first non-whitespace byte, whether the peer uses RFC 6587 octet-counted framing
/// (<c>MSGLEN SP MSG</c>, leading ASCII digit) or non-transparent newline-delimited
/// framing, and applies that for the life of the connection.
///
/// <para>Oversized messages are truncated to <paramref name="maxMessageBytes"/> and flagged
/// (VENDOR_SUPPORT.md "Oversized messages — never drop"); genuinely malformed octet
/// framing (a non-numeric length, an absurd length) throws <see cref="FramingException"/>
/// and the caller drops the connection.</para>
///
/// <para>Not thread-safe: one framer per connection, fed by that connection's read loop.</para>
/// </summary>
internal sealed class SyslogStreamFramer
{
    private const int OctetLengthDigitCap = 12;
    private const long OctetLengthHardCeiling = 64L * 1024 * 1024;

    private readonly int _max;
    private Mode _mode = Mode.Undetermined;
    private byte[] _buf;
    private int _start;
    private int _end;
    private bool _skipToNewline;
    private int _expected = -1;

    public SyslogStreamFramer(int maxMessageBytes)
    {
        _max = maxMessageBytes;
        _buf = new byte[Math.Min(64 * 1024, Math.Max(4096, maxMessageBytes))];
    }

    private enum Mode
    {
        Undetermined,
        OctetCounted,
        NewlineDelimited,
    }

    /// <summary>The framing mode picked for this connection, for tests and diagnostics.</summary>
    public string DetectedFraming => _mode.ToString();

    /// <summary>Feeds a chunk of received bytes, invoking <paramref name="emit"/> once per
    /// complete message with its verbatim payload and a truncation flag.</summary>
    public void Append(ReadOnlySpan<byte> data, Action<byte[], bool> emit)
    {
        EnsureCapacity(data.Length);
        data.CopyTo(_buf.AsSpan(_end));
        _end += data.Length;

        Pump(emit);
    }

    /// <summary>Called when the connection closes: emits a trailing message that had no
    /// final newline (newline framing only).</summary>
    public void Flush(Action<byte[], bool> emit)
    {
        if (_mode != Mode.NewlineDelimited || _skipToNewline)
        {
            return;
        }

        int lineEnd = _end;
        if (lineEnd > _start && _buf[lineEnd - 1] == (byte)'\r')
        {
            lineEnd--;
        }

        int len = lineEnd - _start;
        if (len > 0)
        {
            EmitSlice(_start, len, emit);
        }

        _start = _end;
    }

    private void Pump(Action<byte[], bool> emit)
    {
        while (true)
        {
            if (_mode == Mode.Undetermined)
            {
                while (_start < _end && _buf[_start] is (byte)'\n' or (byte)'\r' or (byte)' ')
                {
                    _start++;
                }

                if (_start >= _end)
                {
                    Compact();
                    return;
                }

                byte first = _buf[_start];
                _mode = first is >= (byte)'1' and <= (byte)'9' ? Mode.OctetCounted : Mode.NewlineDelimited;
            }

            bool progressed = _mode == Mode.OctetCounted ? PumpOctet(emit) : PumpNewline(emit);
            if (!progressed)
            {
                Compact();
                return;
            }
        }
    }

    private bool PumpNewline(Action<byte[], bool> emit)
    {
        int nl = Array.IndexOf(_buf, (byte)'\n', _start, _end - _start);

        if (nl < 0)
        {
            int available = _end - _start;
            if (_skipToNewline)
            {
                _start = _end;
                return false;
            }

            if (available > _max)
            {
                EmitSlice(_start, _max, emit, forceTruncated: true);
                _start += _max;
                _skipToNewline = true;
                return true;
            }

            return false;
        }

        if (_skipToNewline)
        {
            _start = nl + 1;
            _skipToNewline = false;
            return true;
        }

        int lineEnd = nl;
        if (lineEnd > _start && _buf[lineEnd - 1] == (byte)'\r')
        {
            lineEnd--;
        }

        int len = lineEnd - _start;
        if (len > 0)
        {
            EmitSlice(_start, len, emit);
        }

        _start = nl + 1;
        return true;
    }

    private bool PumpOctet(Action<byte[], bool> emit)
    {
        if (_expected < 0)
        {
            int scan = _start;
            while (scan < _end && _buf[scan] is >= (byte)'0' and <= (byte)'9')
            {
                scan++;
            }

            if (scan - _start > OctetLengthDigitCap)
            {
                throw new FramingException("RFC 6587 message length token is too long.");
            }

            if (scan >= _end)
            {
                return false; // still reading digits
            }

            if (_buf[scan] != (byte)' ' || scan == _start)
            {
                throw new FramingException("RFC 6587 frame did not start with a numeric length followed by a space.");
            }

            long n = 0;
            for (int i = _start; i < scan; i++)
            {
                n = (n * 10) + (_buf[i] - (byte)'0');
                if (n > OctetLengthHardCeiling)
                {
                    throw new FramingException("RFC 6587 message length exceeds the hard ceiling.");
                }
            }

            if (n <= 0)
            {
                throw new FramingException("RFC 6587 message length must be positive.");
            }

            _expected = (int)n;
            _start = scan + 1;
        }

        if (_end - _start < _expected)
        {
            return false;
        }

        int storeLen = Math.Min(_expected, _max);
        EmitSlice(_start, storeLen, emit, forceTruncated: _expected > _max);
        _start += _expected;
        _expected = -1;
        return true;
    }

    private void EmitSlice(int offset, int length, Action<byte[], bool> emit, bool forceTruncated = false)
    {
        int copyLen = Math.Min(length, _max);
        bool truncated = forceTruncated || length > _max;
        var payload = new byte[copyLen];
        Buffer.BlockCopy(_buf, offset, payload, 0, copyLen);
        emit(payload, truncated);
    }

    private void EnsureCapacity(int incoming)
    {
        if (_end + incoming <= _buf.Length)
        {
            return;
        }

        Compact();

        if (_end + incoming <= _buf.Length)
        {
            return;
        }

        int needed = _end + incoming;
        int size = _buf.Length;
        while (size < needed)
        {
            size *= 2;
        }

        Array.Resize(ref _buf, size);
    }

    private void Compact()
    {
        if (_start == 0)
        {
            return;
        }

        int len = _end - _start;
        if (len > 0)
        {
            Buffer.BlockCopy(_buf, _start, _buf, 0, len);
        }

        _start = 0;
        _end = len;
    }
}

/// <summary>Thrown when a TCP peer's octet-counted framing is unrecoverably malformed.</summary>
internal sealed class FramingException(string message) : Exception(message);
