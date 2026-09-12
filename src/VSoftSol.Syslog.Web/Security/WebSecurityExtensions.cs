using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Service.Hosting;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>Wires cookie authentication, the policy set, session revalidation, and the CSP nonce accessor.</summary>
public static class WebSecurityExtensions
{
    public static IServiceCollection AddSyslogWebSecurity(this IServiceCollection services, IConfiguration configuration)
    {
        ArgumentNullException.ThrowIfNull(services);
        ArgumentNullException.ThrowIfNull(configuration);

        services.AddHttpContextAccessor();
        services.TryAddSingleton<FirstRunState>();
        services.TryAddSingleton<FirstRunWizardState>();
        services.TryAddScoped<NonceAccessor>();
        services.TryAddScoped<AuthSessionService>();
        services.TryAddScoped<SessionCookieEvents>();
        services.TryAddScoped<CurrentUserAccessor>();
        services.TryAddScoped<UserAdminService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Devices.DeviceAdminService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Streams.StreamAdminService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Rules.RuleAdminService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Alerts.AlertAdminService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Notifications.NotificationService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Dashboards.DashboardService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Dashboards.WidgetDataService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Dashboards.WidgetPickerModel>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Retention.RetentionAdminService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Reports.ReportAdminService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Reports.ReportRenderService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Reports.ReportSmtpAdminService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Reports.IWebHostEnvironmentLogoResolver, VSoftSol.Syslog.Web.Reports.WebHostEnvironmentLogoResolver>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Hardening.MfaSelfServiceService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Hardening.ApiKeyAdminService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Hardening.ListenerSettingsService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Hardening.ConfigBundleAdminService>();
        services.TryAddScoped<VSoftSol.Syslog.Web.Hardening.SelfMonitoringViewService>();

        services.AddOptions<WebAuthOptions>()
            .Bind(configuration.GetSection(WebAuthOptions.SectionName))
            .ValidateDataAnnotations()
            .PostConfigure<IOptions<CollectorOptions>>((web, collector) =>
                web.IdleTimeout = TimeSpan.FromMinutes(collector.Value.UiSessionTimeoutMinutes));

        WebAuthOptions bootstrap = new();
        configuration.GetSection(WebAuthOptions.SectionName).Bind(bootstrap);

        services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
            .AddCookie(CookieAuthenticationDefaults.AuthenticationScheme, options =>
            {
                options.Cookie.Name = bootstrap.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SecurePolicy = CookieSecurePolicy.Always;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.SlidingExpiration = false;
                options.ExpireTimeSpan = bootstrap.AbsoluteSessionLifetime;
                options.LoginPath = "/login";
                options.LogoutPath = "/auth/logout";
                options.AccessDeniedPath = "/denied";
                options.ReturnUrlParameter = "returnUrl";
                options.EventsType = typeof(SessionCookieEvents);
            });

        services.AddAuthorization(options => options.AddSyslogPolicies());
        services.AddCascadingAuthenticationState();
        services.AddScoped<AuthenticationStateProvider, SyslogAuthenticationStateProvider>();

        return services;
    }
}
