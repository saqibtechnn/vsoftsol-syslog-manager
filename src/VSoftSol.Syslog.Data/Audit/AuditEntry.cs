namespace VSoftSol.Syslog.Data.Audit;

/// <summary>
/// One security-relevant event to be appended to the audit log (PHASE_04 build item 6,
/// SECURITY_STANDARDS.md §5.8). Immutable once written; there is no update or delete path.
/// </summary>
/// <param name="Action">Stable verb, e.g. <c>login.success</c>, <c>user.create</c>, <c>config.change</c>. See <see cref="AuditActions"/>.</param>
/// <param name="Actor">Username performing the action, or null for a system action.</param>
/// <param name="EntityType">The kind of thing acted on, e.g. <c>user</c>, <c>listener</c>.</param>
/// <param name="EntityId">Identifier of that thing, as a string.</param>
/// <param name="SourceIp">Remote address of the request, when known.</param>
/// <param name="BeforeJson">Redacted JSON snapshot before the change, or null.</param>
/// <param name="AfterJson">Redacted JSON snapshot after the change, or null.</param>
/// <param name="Detail">Free-text note; never contains a secret value.</param>
public sealed record AuditEntry(
    string Action,
    string? Actor = null,
    string? EntityType = null,
    string? EntityId = null,
    string? SourceIp = null,
    string? BeforeJson = null,
    string? AfterJson = null,
    string? Detail = null);
