namespace VSoftSol.Syslog.Core.Enums;

/// <summary>
/// Outcome of the Phase 3 parse fallback chain: RFC 5424 → RFC 3164 → raw.
/// <see cref="Raw"/> means the original bytes are preserved and the message is still
/// stored, searchable, and alertable (CLAUDE.md Constraint 4).
/// </summary>
public enum ParseStatus
{
    Raw = 0,
    Rfc3164 = 1,
    Rfc5424 = 2,
}
