using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using VSoftSol.Syslog.Core.Devices;

namespace VSoftSol.Syslog.Data.Devices;

/// <summary>
/// Resolves a message's source IP to a <c>device_id</c> on the ingest path, triggering
/// auto-discovery for an unknown source (PHASE_06 build items 2 &amp; 3). An IP is looked
/// up in the database at most once — every subsequent message from it is a cache hit — so
/// 100 000 messages from one unknown source produce exactly one pending record. During a
/// discovery flood the resolver pauses discovery once the pending queue hits its cap, so
/// the database and the UI stay usable while an operator investigates.
/// </summary>
public sealed class DeviceResolver
{
    private static readonly TimeSpan SettingsTtl = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan FloodPause = TimeSpan.FromMinutes(5);

    private readonly SqliteDeviceStore _devices;
    private readonly SqliteDiscoverySettingsStore _settingsStore;
    private readonly TimeProvider _time;
    private readonly ILogger<DeviceResolver> _logger;

    private readonly ConcurrentDictionary<string, long?> _cache = new(StringComparer.Ordinal);
    private DiscoverySettings _settings = new();
    private DateTimeOffset _settingsExpiry;
    private DateTimeOffset _discoveryPausedUntil;
    private long _floodEvents;

    public DeviceResolver(
        SqliteDeviceStore devices,
        SqliteDiscoverySettingsStore settingsStore,
        ILogger<DeviceResolver> logger,
        TimeProvider? timeProvider = null)
    {
        _devices = devices ?? throw new ArgumentNullException(nameof(devices));
        _settingsStore = settingsStore ?? throw new ArgumentNullException(nameof(settingsStore));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _time = timeProvider ?? TimeProvider.System;
    }

    /// <summary>How many times discovery was paused because the pending queue filled (flood signal).</summary>
    public long FloodEventCount => Interlocked.Read(ref _floodEvents);

    public async Task<long?> ResolveAsync(string sourceIp, string? hostnameHint, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sourceIp);

        if (_cache.TryGetValue(sourceIp, out long? cached))
        {
            return cached;
        }

        DateTimeOffset nowUtc = _time.GetUtcNow();
        if (nowUtc < _discoveryPausedUntil)
        {
            _cache[sourceIp] = null; // unattributed while discovery is paused
            return null;
        }

        DiscoverySettings settings = await GetSettingsAsync(nowUtc, cancellationToken).ConfigureAwait(false);
        DeviceRegistration registration =
            await _devices.RegisterDiscoveredAsync(sourceIp, hostnameHint, settings, cancellationToken).ConfigureAwait(false);

        if (registration.PendingQueueFull)
        {
            _discoveryPausedUntil = nowUtc + FloodPause;
            Interlocked.Increment(ref _floodEvents);
            _logger.LogWarning(
                "Auto-discovery paused for {Minutes} min: the pending-device queue is at its cap ({Max}). " +
                "Approve or reject the queued devices, or raise the cap.",
                FloodPause.TotalMinutes, settings.MaxPendingDevices);
        }

        _cache[sourceIp] = registration.DeviceId;
        return registration.DeviceId;
    }

    /// <summary>Drop the cache after a device / discovery-settings change so new mappings take effect.</summary>
    public void Invalidate()
    {
        _cache.Clear();
        _settingsExpiry = default;
        _discoveryPausedUntil = default;
    }

    private async Task<DiscoverySettings> GetSettingsAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        if (nowUtc < _settingsExpiry)
        {
            return _settings;
        }

        _settings = await _settingsStore.GetAsync(cancellationToken).ConfigureAwait(false);
        _settingsExpiry = nowUtc + SettingsTtl;
        return _settings;
    }
}
