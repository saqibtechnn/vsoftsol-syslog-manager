using System.Globalization;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Append-only on-disk overflow for the ingest path (PHASE_02 item 4, ADR 0003 / 0010).
/// Listeners write here when the in-memory channel is full; the pipeline drains it back
/// out, and a segment is deleted only after its frames are committed to the database.
///
/// <para><b>Durability.</b> Writes are buffered and force-flushed to disk
/// (<c>Flush(flushToDisk: true)</c>) every <see cref="IngestionOptions.SpillFlushInterval"/>
/// and whenever a segment is sealed. A hard process kill can lose at most one flush window
/// of not-yet-durable frames; everything below <see cref="DurableFrameCount"/> survives.
/// The drain cursor is fsync'd before consumed segments are deleted, so recovery is
/// at-least-once — a crash mid-commit re-delivers frames, it never loses them.</para>
///
/// <para>One writer (listeners, serialised by <see cref="_writeGate"/>), one reader (the
/// pipeline). Not safe for multiple concurrent readers.</para>
/// </summary>
public sealed class DiskSpillQueue : IAsyncDisposable
{
    private const string SegmentPrefix = "seg-";
    private const string SegmentSuffix = ".dat";
    private const string CursorFile = "cursor.dat";

    private readonly IngestionOptions _options;
    private readonly ILogger<DiskSpillQueue> _logger;
    private readonly string _directory;
    private readonly long _segmentBytes;
    private readonly long _maxBytes;
    private readonly SemaphoreSlim _writeGate = new(1, 1);

    private FileStream? _writeStream;
    private long _writeSegSeq;
    private long _activeDurableLength;
    private long _onDiskBytes;
    private long _pendingFrames;

    private long _cursorSegSeq;
    private long _cursorOffset;

    private CancellationTokenSource? _flushLoopCts;
    private Task _flushLoop = Task.CompletedTask;
    private bool _recovered;
    private bool _disposed;

    public DiskSpillQueue(IOptions<IngestionOptions> options, ILogger<DiskSpillQueue> logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _directory = _options.SpillDirectory;
        ArgumentException.ThrowIfNullOrWhiteSpace(_directory);
        _segmentBytes = _options.SpillSegmentBytes;
        _maxBytes = _options.SpillMaxBytes;
    }

    /// <summary>Frames on disk that are not yet committed to the database.</summary>
    public long PendingFrameCount => Interlocked.Read(ref _pendingFrames);

    /// <summary>Bytes currently occupied by un-drained spill segments.</summary>
    public long ByteDepth => Interlocked.Read(ref _onDiskBytes);

    /// <summary>Frames that are durable on disk (below the last force-flushed position).</summary>
    public long DurableFrameCount { get; private set; }

    public bool IsEmpty => PendingFrameCount == 0;

    /// <summary>
    /// Scans the spill directory, drops a torn write tail from an unclean shutdown, and
    /// opens the write segment. Must be called once before <see cref="EnqueueAsync"/> or
    /// <see cref="LeaseAsync"/>. Returns the number of frames left over from a previous run.
    /// </summary>
    public async Task<long> RecoverAsync(CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            Directory.CreateDirectory(_directory);
            (_cursorSegSeq, _cursorOffset) = ReadCursor();

            long[] segments = ExistingSegmentSequences();

            // Drop segments entirely behind the cursor.
            foreach (long seq in segments.Where(s => s < _cursorSegSeq))
            {
                TryDelete(SegmentPath(seq));
            }

            segments = ExistingSegmentSequences();
            _writeSegSeq = segments.Length > 0 ? segments[^1] : Math.Max(1, _cursorSegSeq);

            if (_cursorSegSeq == 0)
            {
                _cursorSegSeq = _writeSegSeq;
                _cursorOffset = 0;
            }

            // Only the highest segment can have a torn tail (sealed segments are always
            // complete). Truncate it to its last whole record.
            string writePath = SegmentPath(_writeSegSeq);
            long validLength = 0;
            if (File.Exists(writePath))
            {
                validLength = ScanValidLength(writePath);
                await using (var fix = new FileStream(writePath, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
                {
                    if (fix.Length != validLength)
                    {
                        _logger.LogWarning(
                            "Spill segment {Segment} had a {Bytes}-byte torn tail from an unclean shutdown; truncated.",
                            _writeSegSeq, fix.Length - validLength);
                        fix.SetLength(validLength);
                        fix.Flush(flushToDisk: true);
                    }
                }
            }

            _writeStream = new FileStream(
                writePath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read, bufferSize: 1 << 16);
            _writeStream.Seek(0, SeekOrigin.End);
            _activeDurableLength = _writeStream.Length;

            // Pending frame count + on-disk bytes across every segment from the cursor on.
            long pending = 0;
            long bytes = 0;
            foreach (long seq in ExistingSegmentSequences().Where(s => s >= _cursorSegSeq))
            {
                string path = SegmentPath(seq);
                long segValid = seq == _writeSegSeq ? validLength : new FileInfo(path).Length;
                long from = seq == _cursorSegSeq ? _cursorOffset : 0;
                bytes += Math.Max(0, segValid - from);
                pending += CountRecords(path, from, segValid);
            }

            Interlocked.Exchange(ref _pendingFrames, pending);
            Interlocked.Exchange(ref _onDiskBytes, bytes);
            DurableFrameCount = pending;
            _recovered = true;

            _flushLoopCts = new CancellationTokenSource();
            _flushLoop = Task.Run(() => FlushLoopAsync(_flushLoopCts.Token), CancellationToken.None);

            if (pending > 0)
            {
                _logger.LogInformation("Spill recovery: {Frames} frame(s) carried over from a previous run.", pending);
            }

            return pending;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Appends one frame. Returns <c>false</c> when the spill queue is at its hard size cap
    /// (<see cref="IngestionOptions.SpillMaxBytes"/>) — the caller counts a drop and alerts.
    /// Frames already on disk are unaffected.
    /// </summary>
    public async ValueTask<bool> EnqueueAsync(RawFrame frame, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(frame);
        EnsureRecovered();

        byte[] buffer = new byte[FrameCodec.MaxEncodedSize(frame)];
        int written = FrameCodec.Encode(frame, buffer);

        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (Interlocked.Read(ref _onDiskBytes) + written > _maxBytes)
            {
                return false;
            }

            if (_writeStream!.Length >= _segmentBytes)
            {
                await SealAndRollAsync().ConfigureAwait(false);
            }

            await _writeStream!.WriteAsync(buffer.AsMemory(0, written), cancellationToken).ConfigureAwait(false);
            Interlocked.Add(ref _onDiskBytes, written);
            Interlocked.Increment(ref _pendingFrames);
            return true;
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>Forces buffered writes to disk now and updates <see cref="DurableFrameCount"/>.</summary>
    public async Task ForceFlushAsync(CancellationToken cancellationToken)
    {
        EnsureRecovered();
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            FlushWriteStreamLocked();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    /// <summary>
    /// Leases up to <paramref name="maxFrames"/> durable frames from the front of the
    /// queue. The caller commits them, then calls <see cref="SpillLease.CommitAsync"/> to
    /// advance the cursor; abandoning the lease re-delivers the frames on the next call.
    /// Returns <c>null</c> when nothing durable is available.
    /// </summary>
    public async Task<SpillLease?> LeaseAsync(int maxFrames, CancellationToken cancellationToken)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maxFrames);
        EnsureRecovered();

        long segSeq = _cursorSegSeq;
        long offset = _cursorOffset;
        string path = SegmentPath(segSeq);

        if (!File.Exists(path))
        {
            return null;
        }

        long readableEnd = segSeq == Volatile.Read(ref _writeSegSeq)
            ? Interlocked.Read(ref _activeDurableLength)
            : new FileInfo(path).Length;

        if (offset >= readableEnd)
        {
            // Cursor segment exhausted. If a later segment exists, step to it.
            long[] later = ExistingSegmentSequences().Where(s => s > segSeq).ToArray();
            if (later.Length == 0)
            {
                return null;
            }

            return new SpillLease(this, [], later[0], 0, segSeq);
        }

        var frames = new List<RawFrame>(Math.Min(maxFrames, 4096));
        long pos = offset;
        await using (var read = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 1 << 16))
        {
            read.Seek(offset, SeekOrigin.Begin);
            int toRead = (int)Math.Min(readableEnd - offset, 8 * 1024 * 1024);
            byte[] window = new byte[toRead];
            int got = await ReadExactlyAsync(read, window, cancellationToken).ConfigureAwait(false);

            int cursor = 0;
            while (frames.Count < maxFrames
                   && FrameCodec.TryDecode(window.AsSpan(cursor, got - cursor), out RawFrame? frame, out int consumed))
            {
                frames.Add(frame!);
                cursor += consumed;
                pos += consumed;
            }
        }

        if (frames.Count == 0)
        {
            return null;
        }

        return new SpillLease(this, frames, segSeq, pos, segSeq);
    }

    private async Task CommitLeaseAsync(SpillLease lease, CancellationToken cancellationToken)
    {
        await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            _cursorSegSeq = lease.NextSegSeq;
            _cursorOffset = lease.NextOffset;
            WriteCursor(_cursorSegSeq, _cursorOffset);

            Interlocked.Add(ref _pendingFrames, -lease.Frames.Count);

            // Delete sealed segments that are now entirely behind the cursor.
            foreach (long seq in ExistingSegmentSequences().Where(s => s < _cursorSegSeq))
            {
                TryDelete(SegmentPath(seq));
            }

            RecomputeOnDiskBytes();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    // ----- internals -----

    private void EnsureRecovered()
    {
        if (!_recovered)
        {
            throw new InvalidOperationException("DiskSpillQueue.RecoverAsync must be called before use.");
        }
    }

    private async Task SealAndRollAsync()
    {
        FlushWriteStreamLocked();
        await _writeStream!.DisposeAsync().ConfigureAwait(false);

        _writeSegSeq++;
        string next = SegmentPath(_writeSegSeq);
        _writeStream = new FileStream(next, FileMode.CreateNew, FileAccess.Write, FileShare.Read, 1 << 16);
        _activeDurableLength = 0;
        _logger.LogDebug("Rolled to spill segment {Segment}.", _writeSegSeq);
    }

    private void FlushWriteStreamLocked()
    {
        if (_writeStream is null)
        {
            return;
        }

        _writeStream.Flush(flushToDisk: true);
        Interlocked.Exchange(ref _activeDurableLength, _writeStream.Length);
        DurableFrameCount = Interlocked.Read(ref _pendingFrames);
    }

    private async Task FlushLoopAsync(CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                await Task.Delay(_options.SpillFlushInterval, cancellationToken).ConfigureAwait(false);
                await _writeGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                try
                {
                    FlushWriteStreamLocked();
                }
                finally
                {
                    _writeGate.Release();
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "Spill flush loop failed.");
        }
    }

    private (long SegSeq, long Offset) ReadCursor()
    {
        string path = Path.Combine(_directory, CursorFile);
        if (!File.Exists(path))
        {
            return (0, 0);
        }

        string[] parts = File.ReadAllText(path).Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        return parts.Length == 2
            && long.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out long seq)
            && long.TryParse(parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out long off)
            ? (seq, off)
            : (0, 0);
    }

    private void WriteCursor(long segSeq, long offset)
    {
        string path = Path.Combine(_directory, CursorFile);
        string tmp = path + ".tmp";
        string text = string.Create(CultureInfo.InvariantCulture, $"{segSeq} {offset}");
        using (var fs = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None))
        using (var writer = new StreamWriter(fs))
        {
            writer.Write(text);
            writer.Flush();
            fs.Flush(flushToDisk: true);
        }

        File.Move(tmp, path, overwrite: true);
    }

    private long[] ExistingSegmentSequences() =>
        Directory.EnumerateFiles(_directory, SegmentPrefix + "*" + SegmentSuffix)
            .Select(f => Path.GetFileNameWithoutExtension(f)[SegmentPrefix.Length..])
            .Select(s => long.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out long v) ? v : -1)
            .Where(v => v > 0)
            .OrderBy(v => v)
            .ToArray();

    private string SegmentPath(long seq) =>
        Path.Combine(_directory, SegmentPrefix + seq.ToString("D8", CultureInfo.InvariantCulture) + SegmentSuffix);

    private static long ScanValidLength(string path)
    {
        byte[] bytes = File.ReadAllBytes(path);
        long valid = 0;
        int cursor = 0;
        try
        {
            while (FrameCodec.TryDecode(bytes.AsSpan(cursor), out _, out int consumed))
            {
                cursor += consumed;
                valid = cursor;
            }
        }
        catch (InvalidDataException)
        {
            // Corruption partway through — everything before `valid` is still good.
        }

        return valid;
    }

    private static long CountRecords(string path, long from, long to)
    {
        if (to <= from)
        {
            return 0;
        }

        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        fs.Seek(from, SeekOrigin.Begin);
        byte[] bytes = new byte[to - from];
        int got = fs.Read(bytes, 0, bytes.Length);

        long count = 0;
        int cursor = 0;
        try
        {
            while (FrameCodec.TryDecode(bytes.AsSpan(cursor, got - cursor), out _, out int consumed))
            {
                cursor += consumed;
                count++;
            }
        }
        catch (InvalidDataException)
        {
        }

        return count;
    }

    private void RecomputeOnDiskBytes()
    {
        long bytes = 0;
        foreach (long seq in ExistingSegmentSequences().Where(s => s >= _cursorSegSeq))
        {
            long from = seq == _cursorSegSeq ? _cursorOffset : 0;
            long size = seq == _writeSegSeq ? Interlocked.Read(ref _activeDurableLength) : new FileInfo(SegmentPath(seq)).Length;
            bytes += Math.Max(0, size - from);
        }

        Interlocked.Exchange(ref _onDiskBytes, bytes);
    }

    private static async Task<int> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int total = 0;
        while (total < buffer.Length)
        {
            int n = await stream.ReadAsync(buffer.AsMemory(total), cancellationToken).ConfigureAwait(false);
            if (n == 0)
            {
                break;
            }

            total += n;
        }

        return total;
    }

    private bool TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
                return true;
            }
        }
        catch (IOException ex)
        {
            _logger.LogWarning(ex, "Could not delete consumed spill segment {Path}.", path);
        }

        return false;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_flushLoopCts is not null)
        {
            await _flushLoopCts.CancelAsync().ConfigureAwait(false);
            try
            {
                await _flushLoop.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }

            _flushLoopCts.Dispose();
        }

        await _writeGate.WaitAsync().ConfigureAwait(false);
        try
        {
            if (_writeStream is not null)
            {
                _writeStream.Flush(flushToDisk: true);
                await _writeStream.DisposeAsync().ConfigureAwait(false);
                _writeStream = null;
            }
        }
        finally
        {
            _writeGate.Release();
        }

        _writeGate.Dispose();
    }

    /// <summary>A batch of frames leased from the spill queue, pending a database commit.</summary>
    public sealed class SpillLease
    {
        private readonly DiskSpillQueue _queue;

        internal SpillLease(DiskSpillQueue queue, IReadOnlyList<RawFrame> frames, long nextSegSeq, long nextOffset, long fromSegSeq)
        {
            _queue = queue;
            Frames = frames;
            NextSegSeq = nextSegSeq;
            NextOffset = nextOffset;
            FromSegSeq = fromSegSeq;
        }

        public IReadOnlyList<RawFrame> Frames { get; }

        internal long NextSegSeq { get; }

        internal long NextOffset { get; }

        internal long FromSegSeq { get; }

        /// <summary>Advances the durable cursor past these frames and deletes emptied segments.</summary>
        public Task CommitAsync(CancellationToken cancellationToken) => _queue.CommitLeaseAsync(this, cancellationToken);
    }
}
