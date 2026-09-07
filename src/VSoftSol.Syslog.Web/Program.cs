using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Service.Logging;
using VSoftSol.Syslog.Web.Components;
using VSoftSol.Syslog.Web.Components.DesignSystem;
using VSoftSol.Syslog.Web.Search;
using VSoftSol.Syslog.Web.Security;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);

CollectorOptions bootstrapOptions = new();
builder.Configuration.GetSection(CollectorOptions.SectionName).Bind(bootstrapOptions);
Log.Logger = SerilogBootstrap.CreateBaseConfiguration(bootstrapOptions.DataDirectory).CreateLogger();

try
{
    builder.Host.UseSerilog();

    builder.Services.AddRazorComponents().AddInteractiveServerComponents();
    builder.Services.AddSyslogPlatform(builder.Configuration);
    builder.Services.AddSyslogWebSecurity(builder.Configuration);
    builder.Services.AddSyslogDesignSystem();
    builder.Services.AddHsts(options => options.MaxAge = TimeSpan.FromDays(365));

    WebApplication app = builder.Build();

    if (!app.Environment.IsDevelopment())
    {
        app.UseExceptionHandler("/Error", createScopeForErrors: true);
        app.UseHsts();
    }

    app.UseHttpsRedirection();
    app.UseSecurityHeaders();
    app.UseStaticFiles();

    app.UseAuthentication();
    app.UseAuthorization();
    app.UseAntiforgery();

    app.MapAuthEndpoints();
    app.MapSearchEndpoints();
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
