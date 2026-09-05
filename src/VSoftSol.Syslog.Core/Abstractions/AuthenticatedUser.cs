using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Core.Abstractions;

/// <summary>
/// The identity and scope of a successfully authenticated principal, as returned by an
/// <see cref="IAuthenticationProvider"/>. Scope is carried here so the Phase 4 scope
/// filter has a single, authoritative source.
/// </summary>
/// <param name="Username">Stable login name.</param>
/// <param name="DisplayName">Human-friendly name for the UI.</param>
/// <param name="Role">The single application role assigned to this user.</param>
/// <param name="VisibleStreamIds">Streams this user may see; empty means "all streams".</param>
/// <param name="VisibleDeviceGroupIds">Device groups this user may see; empty means "all groups".</param>
/// <param name="MustChangePassword">True when the user must set a new password before proceeding.</param>
public sealed record AuthenticatedUser(
    string Username,
    string DisplayName,
    Role Role,
    IReadOnlyList<long> VisibleStreamIds,
    IReadOnlyList<long> VisibleDeviceGroupIds,
    bool MustChangePassword);
