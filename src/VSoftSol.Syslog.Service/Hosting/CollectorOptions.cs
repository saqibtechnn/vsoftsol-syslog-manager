using System.ComponentModel.DataAnnotations;
using VSoftSol.Syslog.Core;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// Bootstrap settings needed before the database configuration surface is available.
/// Everything else lives in the database and is edited from the web UI
/// (CLAUDE.md Constraint 7). Phase 0 defines the shape only.
/// </summary>
public sealed class CollectorOptions
{
    public const string SectionName = "Collector";

    /// <summary>Directory holding the SQLite database, spill queue, archives, and logs.</summary>
    [Required]
    public string DataDirectory { get; set; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
        BrandingInfo.VendorName,
        BrandingInfo.ProductName);

    /// <summary>Minutes of idle time before an authenticated UI session expires.</summary>
    [Range(5, 1440)]
    public int UiSessionTimeoutMinutes { get; set; } = 30;

    /// <summary>
    /// PHASE_12 / ADR 0005: whether the Web host should also register
    /// <c>AddCollectorRuntime</c> (the listeners, ingest pipeline, rules/alerts/retention
    /// hosted services) alongside its own Kestrel/Blazor pipeline, becoming the single
    /// production Windows Service the installer registers. False by default so
    /// <c>dotnet run --project src/VSoftSol.Syslog.Web</c> stays UI-only in development
    /// (CLAUDE.md's documented dev command) — the packaged <c>appsettings.Production.json</c>
    /// sets this to true, picked up automatically because a Windows-Service-hosted process
    /// has no <c>ASPNETCORE_ENVIRONMENT</c> set and therefore defaults to Production.
    /// </summary>
    public bool HostCollectorRuntime { get; set; }
}
