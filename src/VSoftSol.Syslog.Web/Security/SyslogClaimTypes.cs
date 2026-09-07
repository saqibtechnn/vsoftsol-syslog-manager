namespace VSoftSol.Syslog.Web.Security;

/// <summary>Custom claim types carried on the authentication cookie.</summary>
public static class SyslogClaimTypes
{
    /// <summary>Opaque server-side session id (the cookie is otherwise just a bearer of this).</summary>
    public const string SessionId = "vsoftsol:sid";

    /// <summary>Comma-separated visible stream ids; absent or empty means "all streams".</summary>
    public const string VisibleStreams = "vsoftsol:streams";

    /// <summary>Comma-separated visible device-group ids; absent or empty means "all groups".</summary>
    public const string VisibleDeviceGroups = "vsoftsol:groups";

    /// <summary>"1" while the user must set a new password before using the app.</summary>
    public const string MustChangePassword = "vsoftsol:mustchangepw";
}
