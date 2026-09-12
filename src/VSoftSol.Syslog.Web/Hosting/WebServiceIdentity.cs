using VSoftSol.Syslog.Core;

namespace VSoftSol.Syslog.Web.Hosting;

/// <summary>
/// The Windows Service Control Manager name this host registers under. Per ADR 0005 ("One
/// Windows Service hosting collector and UI, not split processes"), <c>VSoftSol.Syslog.Web</c>
/// is the single production entry point the installer registers as a service — it already
/// hosts Kestrel/Blazor, and conditionally hosts the collector runtime too when
/// <see cref="VSoftSol.Syslog.Service.Hosting.CollectorOptions.HostCollectorRuntime"/> is
/// set (the packaged <c>appsettings.Production.json</c> sets it; a plain
/// <c>dotnet run --project src/VSoftSol.Syslog.Web</c> in development does not, matching
/// CLAUDE.md's documented "run UI standalone" dev command). <c>VSoftSol.Syslog.Service.exe</c>
/// remains a separate, dev-only console-mode collector — the installer never ships or
/// registers it. This exact string must match <c>ServiceInstall/@Name</c> in
/// <c>installer/Product.wxs</c> — <see cref="System.ServiceProcess.ServiceBase"/> fails to
/// start if the name it registers with <c>StartServiceCtrlDispatcher</c> does not match the
/// name SCM has on record for the service.
/// </summary>
public static class WebServiceIdentity
{
    public const string ServiceName = BrandingInfo.ProductName;
}
