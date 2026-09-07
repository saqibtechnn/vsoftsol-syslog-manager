using Microsoft.AspNetCore.Authorization;
using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>
/// The product's authorization policies (PHASE_04 build item 3: "Enforce with policy-based
/// authorization, not role-string checks scattered through components"). Every protected
/// page carries exactly one of these via <c>[Authorize(Policy = ...)]</c>; the
/// authorization-matrix test fails the build if a routable page is missing one.
/// </summary>
public static class AuthPolicies
{
    /// <summary>See events, dashboards, search results (within scope). All four roles.</summary>
    public const string ViewData = "ViewData";

    /// <summary>Create and edit rules, alerts, dashboards, streams. Administrator + Operator.</summary>
    public const string Operate = "Operate";

    /// <summary>System configuration: users, listeners, retention, settings. Administrator only.</summary>
    public const string Administer = "Administer";

    /// <summary>Read the audit log and compliance data. Administrator + Auditor.</summary>
    public const string ViewAudit = "ViewAudit";

    /// <summary>View and run reports. All four roles.</summary>
    public const string ViewReports = "ViewReports";

    /// <summary>Every policy name, for the exhaustive matrix test.</summary>
    public static IReadOnlyList<string> All { get; } = [ViewData, Operate, Administer, ViewAudit, ViewReports];

    /// <summary>Which roles satisfy each policy — the single source of truth for the matrix test and the UI nav.</summary>
    public static IReadOnlyDictionary<string, IReadOnlyList<Role>> RolesFor { get; } =
        new Dictionary<string, IReadOnlyList<Role>>
        {
            [ViewData] = [Role.Administrator, Role.Operator, Role.ReadOnly, Role.Auditor],
            [Operate] = [Role.Administrator, Role.Operator],
            [Administer] = [Role.Administrator],
            [ViewAudit] = [Role.Administrator, Role.Auditor],
            [ViewReports] = [Role.Administrator, Role.Operator, Role.ReadOnly, Role.Auditor],
        };

    public static AuthorizationOptions AddSyslogPolicies(this AuthorizationOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        foreach (string policy in All)
        {
            string[] roles = [.. RolesFor[policy].Select(r => r.ToString())];
            options.AddPolicy(policy, p => p.RequireAuthenticatedUser().RequireRole(roles));
        }

        // Authenticated but no policy — used by pages every signed-in user reaches
        // (the account / password-change area).
        options.AddPolicy("Authenticated", p => p.RequireAuthenticatedUser());

        options.FallbackPolicy = new AuthorizationPolicyBuilder().RequireAuthenticatedUser().Build();
        return options;
    }
}
