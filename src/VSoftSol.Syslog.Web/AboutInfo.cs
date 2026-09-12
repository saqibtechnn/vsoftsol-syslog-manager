using System.Diagnostics;
using System.Reflection;
using VSoftSol.Syslog.Core;

namespace VSoftSol.Syslog.Web;

/// <summary>
/// PHASE_12 build item 3 (version stamping / About page). No licence-gating exists anywhere
/// in this product (CLAUDE.md lists no such requirement), so "licence state" is a fixed,
/// honest statement of the terms every install already accepted at setup, not a trial
/// countdown the product cannot actually enforce.
/// </summary>
public static class AboutInfo
{
    public static string Version => FileVersionInfo.GetVersionInfo(Assembly.GetExecutingAssembly().Location).ProductVersion
        ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "unknown";

    /// <summary>
    /// The build's own file timestamp. Deterministic builds (Directory.Build.props) strip
    /// the embedded PE timestamp for byte-for-byte reproducibility, so this reads the
    /// on-disk file's last-write time instead — set once, at build time, by the compiler,
    /// and never touched again by a normal install or restart.
    /// </summary>
    public static DateTimeOffset BuildDateUtc =>
        new(File.GetLastWriteTimeUtc(Assembly.GetExecutingAssembly().Location), TimeSpan.Zero);

    public static string LicenseState => $"Licensed under your agreement with {BrandingInfo.VendorName}.";
}
