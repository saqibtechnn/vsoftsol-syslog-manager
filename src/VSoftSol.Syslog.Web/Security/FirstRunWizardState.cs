namespace VSoftSol.Syslog.Web.Security;

/// <summary>
/// In-progress answers for the unskippable first-run wizard (PHASE_12 build item 2). A
/// plain in-memory singleton rather than per-step hidden form fields or a database row:
/// exactly one first-run wizard is ever completed per install, sequentially, by the one
/// admin at the keyboard, so there is nothing to key this by and nothing worth persisting
/// past a restart (a restart mid-wizard just starts the wizard over from step 1 — the
/// admin's password is not written to the database until the final step).
/// </summary>
public sealed class FirstRunWizardState
{
    public int Step { get; set; } = 1;

    /// <summary>Held only from step 1 (password entry) to step 5 (finish, where it is
    /// hashed and immediately cleared) — never written to disk, never logged.</summary>
    public string? PendingPassword { get; set; }

    public int UdpPort { get; set; } = 514;

    public int TcpPort { get; set; } = 514;

    public int WebHttpsPort { get; set; } = 5443;

    public string RetentionPresetKey { get; set; } = "medium";
}
