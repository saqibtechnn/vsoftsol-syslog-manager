using System.Globalization;
using System.Security.Claims;
using VSoftSol.Syslog.Core.Abstractions;

namespace VSoftSol.Syslog.Web.Security;

/// <summary>Builds the <see cref="ClaimsPrincipal"/> that backs the auth cookie from an <see cref="AuthenticatedUser"/>.</summary>
public static class PrincipalFactory
{
    public const string AuthenticationType = "VSoftSolCookie";

    public static ClaimsPrincipal Build(AuthenticatedUser user, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(user);
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);

        var claims = new List<Claim>
        {
            new(ClaimTypes.Name, user.Username),
            new(ClaimTypes.Role, user.Role.ToString()),
            new("display_name", user.DisplayName),
            new(SyslogClaimTypes.SessionId, sessionId),
            new(SyslogClaimTypes.VisibleStreams, Join(user.VisibleStreamIds)),
            new(SyslogClaimTypes.VisibleDeviceGroups, Join(user.VisibleDeviceGroupIds)),
        };

        if (user.MustChangePassword)
        {
            claims.Add(new Claim(SyslogClaimTypes.MustChangePassword, "1"));
        }

        return new ClaimsPrincipal(new ClaimsIdentity(claims, AuthenticationType, ClaimTypes.Name, ClaimTypes.Role));
    }

    private static string Join(IReadOnlyList<long> ids) =>
        ids.Count == 0 ? string.Empty : string.Join(',', ids.Select(id => id.ToString(CultureInfo.InvariantCulture)));
}
