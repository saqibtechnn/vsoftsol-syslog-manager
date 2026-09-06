using System.ComponentModel.DataAnnotations;

namespace VSoftSol.Syslog.Ingestion;

/// <summary>
/// Receive-path tuning. Bound from the <c>Ingestion</c> configuration section in the
/// composition root. Everything here has a working default; an out-of-the-box install
/// listens on UDP and TCP 514 with a 100k-frame in-memory buffer backed by a disk spill
/// queue under the collector data directory.
/// </summary>
public sealed class IngestionOptions
{
    public const string SectionName = "Ingestion";

    // ---- UDP listener ----

    /// <summary>Enable the UDP listener.</summary>
    public bool UdpEnabled { get; set; } = true;

    /// <summary>Address the UDP listener binds. <c>0.0.0.0</c> = all interfaces (the norm for
    /// a LAN syslog collector); set a specific address to restrict it.</summary>
    [Required]
    public string UdpBindAddress { get; set; } = "0.0.0.0";

    /// <summary>UDP port. 514 is the syslog well-known port.</summary>
    [Range(1, 65535)]
    public int UdpPort { get; set; } = 514;

    /// <summary>Socket receive-buffer size (SO_RCVBUF). Large, so a burst that outruns the
    /// receive loop is absorbed by the kernel rather than dropped as ICMP port-unreachable.</summary>
    [Range(64 * 1024, 128 * 1024 * 1024)]
    public int UdpReceiveBufferBytes { get; set; } = 8 * 1024 * 1024;

    // ---- TCP listener ----

    /// <summary>Enable the TCP listener.</summary>
    public bool TcpEnabled { get; set; } = true;

    /// <summary>Address the TCP listener binds.</summary>
    [Required]
    public string TcpBindAddress { get; set; } = "0.0.0.0";

    /// <summary>TCP port.</summary>
    [Range(1, 65535)]
    public int TcpPort { get; set; } = 514;

    /// <summary>Maximum simultaneous TCP connections. New connections beyond this are
    /// accepted and immediately closed (with a counter) so a connection flood cannot
    /// exhaust memory or file handles.</summary>
    [Range(1, 100_000)]
    public int TcpMaxConnections { get; set; } = 1_000;

    /// <summary>A TCP connection with no bytes for this long is closed. Bounds slowloris /
    /// half-open connection cost.</summary>
    [Range(typeof(TimeSpan), "00:00:05", "01:00:00")]
    public TimeSpan TcpIdleTimeout { get; set; } = TimeSpan.FromMinutes(5);

    // ---- framing ----

    /// <summary>Largest single message accepted. Bytes beyond this are still stored
    /// (truncated to this length) and the event is flagged <c>truncated</c> — never dropped
    /// (VENDOR_SUPPORT.md "Oversized messages"). Guards against a single frame forcing an
    /// unbounded allocation.</summary>
    [Range(480, 16 * 1024 * 1024)]
    public int MaxMessageBytes { get; set; } = 64 * 1024;

    // ---- bounded channel ----

    /// <summary>In-memory hand-off capacity between the listeners and the writer. When full,
    /// new frames go to the disk spill queue instead of blocking the receive loop.</summary>
    [Range(1_000, 10_000_000)]
    public int ChannelCapacity { get; set; } = 100_000;

    // ---- disk spill queue ----

    /// <summary>Directory for spill segments. Empty = <c>&lt;DataDirectory&gt;/spill</c>,
    /// resolved in the composition root.</summary>
    public string SpillDirectory { get; set; } = string.Empty;

    /// <summary>Roll to a new spill segment once the current one reaches this size.</summary>
    [Range(1 * 1024 * 1024, 1024L * 1024 * 1024)]
    public long SpillSegmentBytes { get; set; } = 64 * 1024 * 1024;

    /// <summary>Hard cap on total un-drained spill on disk. Once reached, further frames are
    /// dropped-with-counter and an alert is raised rather than filling the disk
    /// (SECURITY_STANDARDS.md §2 "Resource exhaustion"). Accepted frames already on disk
    /// are still never lost.</summary>
    [Range(16L * 1024 * 1024, 1024L * 1024 * 1024 * 1024)]
    public long SpillMaxBytes { get; set; } = 4L * 1024 * 1024 * 1024;

    /// <summary>How often the spill queue forces buffered writes to disk (group commit). A
    /// hard process kill can lose at most this window of not-yet-fsynced frames; everything
    /// older is durable.</summary>
    [Range(typeof(TimeSpan), "00:00:00.020", "00:00:05")]
    public TimeSpan SpillFlushInterval { get; set; } = TimeSpan.FromMilliseconds(100);

    // ---- writer / batching ----

    /// <summary>Rows per insert transaction handed to the repository.</summary>
    [Range(1, 100_000)]
    public int BatchSize { get; set; } = 1_000;

    /// <summary>Longest the pipeline waits to fill a batch before committing a partial one.</summary>
    [Range(typeof(TimeSpan), "00:00:00.010", "00:00:05")]
    public TimeSpan BatchLinger { get; set; } = TimeSpan.FromMilliseconds(200);

    // ---- shutdown ----

    /// <summary>On stop, the pipeline is given this long to drain the channel and spill
    /// queue and commit. If it is exceeded the remainder stays on disk for the next start
    /// (still not lost) and the overrun is logged.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "00:10:00")]
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(30);

    // ---- per-source rate limiting ----

    /// <summary>Sustained frames/second allowed per source IP. 0 disables rate limiting
    /// (the default — a flapping interface legitimately bursts and must not be lost).</summary>
    [Range(0, 10_000_000)]
    public int PerSourceRatePerSecond { get; set; }

    /// <summary>Token-bucket depth as a multiple of <see cref="PerSourceRatePerSecond"/>;
    /// how large an instantaneous burst a source may send before the ceiling bites.</summary>
    [Range(1, 1_000)]
    public int PerSourceBurstMultiplier { get; set; } = 4;

    /// <summary>What a listener does with frames from a source over its ceiling.</summary>
    public RateLimitBreachBehavior RateLimitBreachBehavior { get; set; } = RateLimitBreachBehavior.Throttle;

    /// <summary>Delay applied to the receive path for an over-ceiling frame under
    /// <see cref="RateLimitBreachBehavior.Throttle"/>. Small — enough to cap a runaway
    /// source without losing anything.</summary>
    [Range(typeof(TimeSpan), "00:00:00", "00:00:01")]
    public TimeSpan ThrottleDelay { get; set; } = TimeSpan.FromMilliseconds(1);

    /// <summary>How long a source stays quarantined after a breach when
    /// <see cref="RateLimitBreachBehavior"/> is <see cref="RateLimitBreachBehavior.Quarantine"/>.</summary>
    [Range(typeof(TimeSpan), "00:00:01", "01:00:00")]
    public TimeSpan QuarantineDuration { get; set; } = TimeSpan.FromMinutes(1);
}
