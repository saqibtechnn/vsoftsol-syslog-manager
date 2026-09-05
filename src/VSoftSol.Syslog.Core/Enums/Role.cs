namespace VSoftSol.Syslog.Core.Enums;

/// <summary>
/// The four fixed application roles (PHASE_04). A role assignment additionally carries
/// the set of visible streams and device groups; authorization is policy-based, never a
/// scattered role-string check.
/// </summary>
public enum Role
{
    /// <summary>Full control: configuration, users, retention, everything.</summary>
    Administrator = 0,

    /// <summary>Day-to-day operation: search, rules, alerts, dashboards within scope.</summary>
    Operator = 1,

    /// <summary>Search and view within scope. No configuration.</summary>
    ReadOnly = 2,

    /// <summary>Read-only across audit log, reports, and archived data for compliance.</summary>
    Auditor = 3,
}
