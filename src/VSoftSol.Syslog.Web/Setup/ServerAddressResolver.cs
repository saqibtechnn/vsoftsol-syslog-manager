using System.Net;
using System.Net.Sockets;

namespace VSoftSol.Syslog.Web.Setup;

/// <summary>
/// Picks the IP address shown on the "Waiting for messages" page's copy-ready device
/// commands (PHASE_12 build item 2a). Prefers the host the admin's own browser used to reach
/// the dashboard (already a real, reachable LAN address or hostname in the normal case,
/// CLAUDE.md Constraint 5) — only falls back to guessing a local IP when that host is a
/// loopback address, which is not something another device on the network could send to.
/// </summary>
public static class ServerAddressResolver
{
    public static string Resolve(string requestHost)
    {
        if (!string.IsNullOrWhiteSpace(requestHost) && !IsLoopback(requestHost))
        {
            return requestHost;
        }

        return FirstNonLoopbackIPv4() ?? requestHost;
    }

    private static bool IsLoopback(string host) =>
        string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase) ||
        (IPAddress.TryParse(host, out IPAddress? ip) && IPAddress.IsLoopback(ip));

    private static string? FirstNonLoopbackIPv4()
    {
        try
        {
            return Dns.GetHostAddresses(Dns.GetHostName())
                .FirstOrDefault(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a))
                ?.ToString();
        }
        catch (SocketException)
        {
            return null;
        }
    }
}
