using System.Net;
using System.Net.Sockets;

namespace VSoftSol.Syslog.Rules.Actions;

/// <summary>
/// SSRF defence for the webhook and syslog-forward actions (PHASE_07 Security Validation).
/// Resolves a host to <b>every</b> address it maps to and rejects the action if any of them
/// is loopback, link-local (incl. the cloud metadata range <c>169.254.0.0/16</c>), a
/// private RFC1918 / CGNAT range, or a unique-local IPv6 address — unless the operator has
/// explicitly allow-listed that exact CIDR on the action.
/// </summary>
public static class PrivateNetworkGuard
{
    /// <summary>
    /// Returns null when every resolved address is a routable public address (or explicitly
    /// allow-listed); otherwise a human reason the action is refused.
    /// </summary>
    public static async Task<string?> CheckAsync(
        string host, IReadOnlyList<string> allowedCidrs, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(host))
        {
            return "the target host is empty";
        }

        IPAddress[] addresses;
        if (IPAddress.TryParse(host, out IPAddress? literal))
        {
            addresses = [literal];
        }
        else
        {
            try
            {
                addresses = await Dns.GetHostAddressesAsync(host, cancellationToken).ConfigureAwait(false);
            }
            catch (SocketException ex)
            {
                return $"the target host '{host}' does not resolve ({ex.SocketErrorCode})";
            }

            if (addresses.Length == 0)
            {
                return $"the target host '{host}' does not resolve";
            }
        }

        List<(IPAddress Network, int Prefix)> allow = ParseCidrs(allowedCidrs);

        foreach (IPAddress address in addresses)
        {
            if (IsBlocked(address) && !allow.Any(c => InRange(address, c.Network, c.Prefix)))
            {
                return $"the target resolves to {address}, which is a private/link-local/loopback address " +
                       "(add its CIDR to the webhook's private-network allow-list to override)";
            }
        }

        return null;
    }

    /// <summary>The synchronous check for a single literal address (used at compile time).</summary>
    public static bool IsBlocked(IPAddress address)
    {
        if (IPAddress.IsLoopback(address))
        {
            return true;
        }

        if (address.AddressFamily == AddressFamily.InterNetwork)
        {
            byte[] b = address.GetAddressBytes();
            return b[0] == 10                                   // 10.0.0.0/8
                || (b[0] == 172 && b[1] >= 16 && b[1] <= 31)    // 172.16.0.0/12
                || (b[0] == 192 && b[1] == 168)                 // 192.168.0.0/16
                || (b[0] == 169 && b[1] == 254)                 // 169.254.0.0/16 link-local + cloud metadata
                || (b[0] == 100 && b[1] >= 64 && b[1] <= 127)   // 100.64.0.0/10 CGNAT
                || b[0] == 0                                    // 0.0.0.0/8
                || b[0] >= 224;                                 // multicast / reserved
        }

        if (address.AddressFamily == AddressFamily.InterNetworkV6)
        {
            if (address.IsIPv6LinkLocal || address.IsIPv6SiteLocal || address.IsIPv6Multicast)
            {
                return true;
            }

            byte[] b = address.GetAddressBytes();
            if ((b[0] & 0xFE) == 0xFC)   // fc00::/7 unique-local
            {
                return true;
            }

            if (address.IsIPv4MappedToIPv6)
            {
                return IsBlocked(address.MapToIPv4());
            }
        }

        return false;
    }

    private static List<(IPAddress, int)> ParseCidrs(IReadOnlyList<string> cidrs)
    {
        var result = new List<(IPAddress, int)>();
        foreach (string entry in cidrs)
        {
            string[] parts = entry.Split('/', 2);
            if (parts.Length == 2
                && IPAddress.TryParse(parts[0], out IPAddress? network)
                && int.TryParse(parts[1], out int prefix))
            {
                result.Add((network, prefix));
            }
        }

        return result;
    }

    private static bool InRange(IPAddress address, IPAddress network, int prefix)
    {
        if (address.AddressFamily != network.AddressFamily)
        {
            return false;
        }

        byte[] a = address.GetAddressBytes();
        byte[] n = network.GetAddressBytes();
        int fullBytes = prefix / 8;
        int remBits = prefix % 8;

        for (int i = 0; i < fullBytes; i++)
        {
            if (a[i] != n[i])
            {
                return false;
            }
        }

        if (remBits == 0)
        {
            return true;
        }

        int mask = 0xFF << (8 - remBits) & 0xFF;
        return (a[fullBytes] & mask) == (n[fullBytes] & mask);
    }
}
