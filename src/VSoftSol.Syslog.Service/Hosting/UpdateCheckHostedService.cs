using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using VSoftSol.Syslog.Data.Updates;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// Runs <see cref="UpdateChecker.RunOnceAsync"/> on an interval read fresh from the database
/// every tick (v1.1 — ADR 0021), so an Administrator's interval change on the Settings page
/// takes effect on the next tick with no restart — the same live-reload spirit as the SNMP
/// community string. Registered in <c>AddSyslogPlatform</c>, not <c>AddCollectorRuntime</c>,
/// so a standalone Web host (not hosting the collector runtime) can still show "update
/// available." A short initial delay avoids competing with startup I/O; every tick's
/// exceptions are logged and swallowed, matching <c>SelfMonitoringService</c>'s discipline —
/// <see cref="UpdateChecker.RunOnceAsync"/> itself never throws, but nothing here should
/// ever take the loop down regardless.
/// </summary>
public sealed class UpdateCheckHostedService : BackgroundService
{
    private static readonly TimeSpan InitialDelay = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan FallbackPollInterval = TimeSpan.FromMinutes(30);

    private readonly UpdateChecker _checker;
    private readonly SqliteUpdateSettingsStore _settings;
    private readonly TimeProvider _time;
    private readonly ILogger<UpdateCheckHostedService> _logger;

    public UpdateCheckHostedService(
        UpdateChecker checker, SqliteUpdateSettingsStore settings, TimeProvider time, ILogger<UpdateCheckHostedService> logger)
    {
        _checker = checker;
        _settings = settings;
        _time = time;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Delay(InitialDelay, stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        DateTimeOffset lastRun = DateTimeOffset.MinValue;
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                UpdateSettings settings = await _settings.GetAsync(stoppingToken).ConfigureAwait(false);
                TimeSpan interval = TimeSpan.FromHours(Math.Max(1, settings.CheckIntervalHours));
                if (_time.GetUtcNow() - lastRun >= interval)
                {
                    await _checker.RunOnceAsync(stoppingToken).ConfigureAwait(false);
                    lastRun = _time.GetUtcNow();
                }
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                _logger.LogWarning(ex, "Self-update check tick failed; will retry.");
            }

            try
            {
                // Poll at a short, fixed cadence rather than sleeping for the full
                // (admin-editable) interval, so a changed interval or a freshly-enabled
                // toggle is picked up promptly rather than only after whatever the old
                // interval happened to be.
                await Task.Delay(FallbackPollInterval, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }
}
