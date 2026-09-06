using System.Threading.Channels;
using Microsoft.Extensions.Options;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// The bounded in-memory hand-off between the listeners and the writer (PHASE_02 item 3,
/// ADR 0003). <see cref="BoundedChannelFullMode.Wait"/> means a producer that calls
/// <see cref="WriteAsync"/> blocks when the channel is full; listeners instead call
/// <see cref="TryWrite"/> and divert to the disk spill queue on a <c>false</c>, so the
/// receive loop never blocks on the writer.
/// </summary>
public sealed class IngestionChannel
{
    private readonly Channel<RawFrame> _channel;

    public IngestionChannel(IOptions<IngestionOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);
        Capacity = options.Value.ChannelCapacity;
        _channel = Channel.CreateBounded<RawFrame>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    }

    public int Capacity { get; }

    /// <summary>Approximate current depth.</summary>
    public int Depth => _channel.Reader.Count;

    public ChannelReader<RawFrame> Reader => _channel.Reader;

    /// <summary>Non-blocking enqueue. <c>false</c> means the channel is full — spill instead.</summary>
    public bool TryWrite(RawFrame frame) => _channel.Writer.TryWrite(frame);

    /// <summary>Blocking enqueue, used by the spill-drain path where back-pressure is wanted.</summary>
    public ValueTask WriteAsync(RawFrame frame, CancellationToken cancellationToken) =>
        _channel.Writer.WriteAsync(frame, cancellationToken);

    /// <summary>No more frames will be written; the reader drains what remains and completes.</summary>
    public void Complete() => _channel.Writer.TryComplete();
}
