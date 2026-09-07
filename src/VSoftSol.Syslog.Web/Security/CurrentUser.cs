using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Components.Authorization;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Security;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>
/// Read-only view of the signed-in principal for components and services. Resolves the
/// <see cref="UserScope"/> once from the cookie claims so the scope chokepoint always has
/// an authoritative source (PHASE_04 build item 4).
/// </summary>
public sealed class CurrentUser
{
    private readonly ClaimsPrincipal _principal;

    public CurrentUser(ClaimsPrincipal principal) => _principal = principal ?? new ClaimsPrincipal(new ClaimsIdentity());

    public bool IsAuthenticated => _principal.Identity?.IsAuthenticated == true;

    public string UserName => _principal.Identity?.Name ?? string.Empty;

    public string DisplayName => _principal.FindFirstValue("display_name") ?? UserName;

    public string? SessionId => _principal.FindFirstValue(SyslogClaimTypes.SessionId);

    public bool MustChangePassword => _principal.FindFirstValue(SyslogClaimTypes.MustChangePassword) == "1";

    public Role? Role =>
        Enum.TryParse(_principal.FindFirstValue(ClaimTypes.Role), out Role role) ? role : null;

    public UserScope Scope => UserScope.Create(
        ParseIds(_principal.FindFirstValue(SyslogClaimTypes.VisibleStreams)),
        ParseIds(_principal.FindFirstValue(SyslogClaimTypes.VisibleDeviceGroups)));

    internal ClaimsPrincipal Principal => _principal;

    private static IEnumerable<long> ParseIds(string? csv)
    {
        if (string.IsNullOrWhiteSpace(csv))
        {
            yield break;
        }

        foreach (string part in csv.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (long.TryParse(part, NumberStyles.Integer, CultureInfo.InvariantCulture, out long id))
            {
                yield return id;
            }
        }
    }
}
