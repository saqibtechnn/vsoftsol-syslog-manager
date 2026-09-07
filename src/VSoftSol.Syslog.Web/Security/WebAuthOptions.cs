using System.ComponentModel.DataAnnotations;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>UI session policy (PHASE_04 build item 5). Bound from the <c>WebAuth</c> section.</summary>
public sealed class WebAuthOptions
{
    public const string SectionName = "WebAuth";

    /// <summary>Idle time before a session expires. Overridden by <c>Collector:UiSessionTimeoutMinutes</c> when set.</summary>
    [Range(typeof(TimeSpan), "00:05:00", "24:00:00")]
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(30);

    /// <summary>Hard cap on a session's lifetime regardless of activity.</summary>
    [Range(typeof(TimeSpan), "00:30:00", "168:00:00")]
    public TimeSpan AbsoluteSessionLifetime { get; set; } = TimeSpan.FromHours(12);

    /// <summary>How often a live Blazor circuit re-checks the principal against the store.</summary>
    [Range(typeof(TimeSpan), "00:00:15", "00:30:00")]
    public TimeSpan RevalidationInterval { get; set; } = TimeSpan.FromSeconds(60);

    /// <summary>Cookie name for the authentication ticket.</summary>
    public string CookieName { get; set; } = "vsoftsol.auth";
}
