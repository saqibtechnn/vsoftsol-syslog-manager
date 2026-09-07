using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace VSoftSol.Syslog.Web.Components.DesignSystem;

/// <summary>DI registration for the shared UI services (PHASE_04 design system).</summary>
public static class DesignSystemExtensions
{
    public static IServiceCollection AddSyslogDesignSystem(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);
        services.TryAddScoped<ToastService>();
        services.TryAddScoped<TimeRangeState>();
        return services;
    }
}
