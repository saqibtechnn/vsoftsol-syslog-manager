using System.ComponentModel.DataAnnotations;

namespace VSoftSol.Syslog.Data.Users;

/// <summary>
/// Local authentication policy (PHASE_04 build item 5). Bound from the
/// <c>Authentication</c> configuration section; editable from the UI Settings area.
/// </summary>
public sealed class AuthenticationOptions
{
    public const string SectionName = "Authentication";

    /// <summary>Consecutive failed logins that trigger a lockout. 0 disables lockout (not recommended).</summary>
    [Range(0, 100)]
    public int LockoutThreshold { get; set; } = 5;

    /// <summary>How long an account stays locked after the threshold is reached.</summary>
    [Range(typeof(TimeSpan), "00:01:00", "24:00:00")]
    public TimeSpan LockoutDuration { get; set; } = TimeSpan.FromMinutes(15);

    /// <summary>Minimum length for a user-chosen password.</summary>
    [Range(8, 256)]
    public int MinimumPasswordLength { get; set; } = 12;
}
