using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Service.Logging;
using VSoftSol.Syslog.Web.Components;
using VSoftSol.Syslog.Web.Components.DesignSystem;
using VSoftSol.Syslog.Web.Hardening;
using VSoftSol.Syslog.Web.Reports;
using VSoftSol.Syslog.Web.Search;
using VSoftSol.Syslog.Web.Security;
using VSoftSol.Syslog.Web.Setup;
using VSoftSol.Syslog.Web.Updates;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

CollectorOptions bootstrapOptions = new();
builder.Configuration.GetSection(CollectorOptions.SectionName).Bind(bootstrapOptions);
Log.Logger = SerilogBootstrap.CreateBaseConfiguration(bootstrapOptions.DataDirectory).CreateLogger();

// PHASE_12 build item 2: same override file the collector reads — Kestrel binds its Https
// endpoint URL from IConfiguration automatically, so this must be added before Build().
BootstrapConfigOverrides.Apply(builder.Configuration, bootstrapOptions.DataDirectory);

try
{
    builder.Host.UseSerilog();
    builder.Host.UseWindowsService(options => options.ServiceName = VSoftSol.Syslog.Web.Hosting.WebServiceIdentity.ServiceName);

    // PHASE_12 build item 1: a self-signed HTTPS certificate, generated on first run if
    // none exists yet, so the service never fails to bind its HTTPS endpoint out of the
    // box. Configured here (before DI) because Kestrel needs it to bind at all.
    builder.WebHost.ConfigureKestrel(kestrel =>
    {
        kestrel.ConfigureHttpsDefaults(https =>
            https.ServerCertificate = VSoftSol.Syslog.Web.Hosting.WebCertificateProvisioning.EnsureCertificate(bootstrapOptions.DataDirectory));
    });

    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    builder.Services.AddSyslogPlatform(builder.Configuration);
    builder.Services.AddSyslogWebSecurity(builder.Configuration);
    builder.Services.AddSyslogDesignSystem();
    builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

    // ADR 0005 / PHASE_12: the packaged installer runs this as the single production
    // Windows Service, so it also hosts the collector runtime here — never in a plain
    // `dotnet run` dev session, where appsettings.Development.json leaves this false.
    if (bootstrapOptions.HostCollectorRuntime)
    {
        builder.Services.AddCollectorRuntime(builder.Configuration);
    }

    WebApplication app = builder.Build();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseSecurityHeaders();
    app.UseStaticFiles();

    // PHASE_12 build item 2: unskippable first-run wizard. Before auth, so it never races
    // the normal challenge-redirect-to-/login logic.
    app.UseFirstRunGate();

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapAuthEndpoints();
    app.MapSearchEndpoints();
    app.MapReportEndpoints();
    app.MapBundleEndpoints();
    app.MapSetupEndpoints();
    app.MapUpdateEndpoints();
    app.MapRazorComponents<App>().AddInteractiveServerRenderMode();

    app.Run();
}
finally
{
    Log.CloseAndFlush();
}

/// <summary>Exposed so integration tests can host the app with WebApplicationFactory.</summary>
public partial class Program
{
}
