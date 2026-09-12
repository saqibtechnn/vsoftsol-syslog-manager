using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Serilog;
using VSoftSol.Syslog.Core;
using VSoftSol.Syslog.Service.Hosting;
using VSoftSol.Syslog.Service.Logging;

HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

// Serilog needs the data directory, which comes from configuration before DI is built.
CollectorOptions bootstrapOptions = new();
builder.Configuration.GetSection(CollectorOptions.SectionName).Bind(bootstrapOptions);
Log.Logger = SerilogBootstrap.CreateBaseConfiguration(bootstrapOptions.DataDirectory).CreateLogger();

// PHASE_12 build item 2: the first-run wizard's listener-ports step writes here; layer it
// after the compiled-in appsettings.json so a wizard-configured port always wins.
BootstrapConfigOverrides.Apply(builder.Configuration, bootstrapOptions.DataDirectory);

try
{
    builder.Services.AddSerilog();
    builder.Services.AddWindowsService(options => options.ServiceName = BrandingInfo.ProductName);
    builder.Services.AddSyslogPlatform(builder.Configuration);
    builder.Services.AddCollectorRuntime(builder.Configuration);

    using IHost host = builder.Build();

    // Fail fast on invalid options rather than starting a half-configured collector.
    _ = host.Services.GetRequiredService<IOptions<CollectorOptions>>().Value;

    await host.RunAsync().ConfigureAwait(false);
}
finally
{
    await Log.CloseAndFlushAsync().ConfigureAwait(false);
}
