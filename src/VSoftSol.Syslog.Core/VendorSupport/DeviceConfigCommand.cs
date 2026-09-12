namespace VSoftSol.Syslog.Core.VendorSupport;

/// <summary>
/// One vendor's device configuration block, parsed from VENDOR_SUPPORT.md (PHASE_12 build
/// item 2a). <see cref="BodyText"/> keeps the <c>&lt;SYSLOG_IP&gt;</c> / <c>&lt;PORT&gt;</c>
/// placeholders literal until <see cref="Render"/> substitutes them at display time — the
/// two documented tokens the "Waiting for messages" page fills in; every other placeholder
/// (<c>&lt;INTERFACE&gt;</c>, <c>&lt;MGMT_IP&gt;</c>, ...) is the admin's own value and stays
/// untouched.
/// </summary>
public sealed record DeviceConfigCommand(string VendorName, string BodyText, bool IsCommandBlock)
{
    public string Render(string syslogIp, int port) =>
        BodyText.Replace("<SYSLOG_IP>", syslogIp, StringComparison.Ordinal)
                .Replace("<PORT>", port.ToString(System.Globalization.CultureInfo.InvariantCulture), StringComparison.Ordinal);
}
