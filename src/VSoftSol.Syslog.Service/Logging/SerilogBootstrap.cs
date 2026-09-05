using Serilog;
using Serilog.Events;
using VSoftSol.Syslog.Core;

namespace VSoftSol.Syslog.Service.Logging;

/// <summary>
/// Internal (product) logging configuration: rolling file plus the Windows Event Log.
/// CLAUDE.md: Serilog with structured properties; never log message payloads at Debug
/// in production paths (syslog bodies can carry credentials).
/// </summary>
public static class SerilogBootstrap
{
    /// <summary>
    /// Builds the base Serilog configuration (rolling file, plus the Windows Event Log on
    /// Windows). The caller composes it into the host logger.
    /// </summary>
    /// <param name="dataDirectory">Root data directory; logs are written under <c>logs/</c>.</param>
    public static LoggerConfiguration CreateBaseConfiguration(string dataDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(dataDirectory);

        string logDirectory = ResolveWritableLogDirectory(dataDirectory);

        LoggerConfiguration configuration = new LoggerConfiguration()
            .MinimumLevel.Information()
            .MinimumLevel.Override("Microsoft", LogEventLevel.Warning)
            .MinimumLevel.Override("Microsoft.Hosting.Lifetime", LogEventLevel.Information)
            .Enrich.FromLogContext()
            .Enrich.WithProperty("Product", BrandingInfo.ProductName)
            .WriteTo.File(
                path: Path.Combine(logDirectory, "syslog-manager-.log"),
                rollingInterval: RollingInterval.Day,
                retainedFileCountLimit: 31,
                fileSizeLimitBytes: 64L * 1024 * 1024,
                rollOnFileSizeLimit: true,
                shared: true,
                outputTemplate: "{Timestamp:yyyy-MM-ddTHH:mm:ss.fffZ} [{Level:u3}] {SourceContext} {Message:lj} {Properties:j}{NewLine}{Exception}");

        if (OperatingSystem.IsWindows())
        {
            configuration = configuration.WriteTo.EventLog(
                source: BrandingInfo.ProductName,
                logName: "Application",
                manageEventSource: false,
                restrictedToMinimumLevel: LogEventLevel.Warning);
        }

        return configuration;
    }

    /// <summary>
    /// Returns <c>&lt;dataDirectory&gt;/logs</c>, creating it. If that directory cannot be
    /// created (a misconfigured or not-yet-provisioned data directory, or a locked-down
    /// test host), falls back to a temp location so logging never blocks startup.
    /// </summary>
    private static string ResolveWritableLogDirectory(string dataDirectory)
    {
        string preferred = Path.Combine(dataDirectory, "logs");
        try
        {
            Directory.CreateDirectory(preferred);
            return preferred;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or NotSupportedException)
        {
            string fallback = Path.Combine(Path.GetTempPath(), "syslog-manager", "logs");
            Directory.CreateDirectory(fallback);
            Console.Error.WriteLine(
                $"Could not use log directory '{preferred}' ({ex.GetType().Name}); falling back to '{fallback}'.");
            return fallback;
        }
    }
}
