using VSoftSol.Syslog.Core.Abstractions;

namespace VSoftSol.Syslog.Core.Security;

/// <summary>
/// The set of events a principal is permitted to see, derived once from
/// <see cref="AuthenticatedUser"/> and then threaded through the single scope chokepoint
/// (<c>ScopedEventReader</c>) — no page queries the event store without it (PHASE_04
/// build item 4).
/// </summary>
/// <remarks>
/// Two independent dimensions, combined with AND (the most restrictive reading, per
/// <see cref="AuthenticatedUser"/>'s contract that an empty set means "all"):
/// an event is visible iff
/// <c>(AllStreams || event is in a visible stream) &amp;&amp; (AllDeviceGroups || event's device is in a visible group)</c>.
/// An event with no stream membership is invisible to a stream-restricted user; an event
/// with no device is invisible to a device-group-restricted user. Fail closed.
/// </remarks>
public sealed class UserScope
{
    private readonly HashSet<long> _streamIds;
    private readonly HashSet<long> _deviceGroupIds;

    private UserScope(bool allStreams, HashSet<long> streamIds, bool allDeviceGroups, HashSet<long> deviceGroupIds)
    {
        AllStreams = allStreams;
        AllDeviceGroups = allDeviceGroups;
        _streamIds = streamIds;
        _deviceGroupIds = deviceGroupIds;
    }

    /// <summary>No stream restriction (the user's visible-stream set was empty).</summary>
    public bool AllStreams { get; }

    /// <summary>No device-group restriction (the user's visible-group set was empty).</summary>
    public bool AllDeviceGroups { get; }

    /// <summary>Visible stream ids. Meaningful only when <see cref="AllStreams"/> is false.</summary>
    public IReadOnlyCollection<long> StreamIds => _streamIds;

    /// <summary>Visible device-group ids. Meaningful only when <see cref="AllDeviceGroups"/> is false.</summary>
    public IReadOnlyCollection<long> DeviceGroupIds => _deviceGroupIds;

    /// <summary>True when the principal may see every event (an Administrator, typically).</summary>
    public bool IsUnrestricted => AllStreams && AllDeviceGroups;

    /// <summary>A scope that permits everything.</summary>
    public static UserScope Unrestricted { get; } = new(true, [], true, []);

    /// <summary>
    /// Builds a scope from an authenticated principal. Empty <see cref="AuthenticatedUser.VisibleStreamIds"/>
    /// / <see cref="AuthenticatedUser.VisibleDeviceGroupIds"/> means "no restriction on that dimension".
    /// </summary>
    public static UserScope FromUser(AuthenticatedUser user)
    {
        ArgumentNullException.ThrowIfNull(user);
        return Create(user.VisibleStreamIds, user.VisibleDeviceGroupIds);
    }

    /// <summary>Builds a scope directly. An empty / null set on a dimension means "all" for that dimension.</summary>
    public static UserScope Create(IEnumerable<long>? streamIds, IEnumerable<long>? deviceGroupIds)
    {
        HashSet<long> streams = streamIds is null ? [] : [.. streamIds];
        HashSet<long> groups = deviceGroupIds is null ? [] : [.. deviceGroupIds];
        return new UserScope(streams.Count == 0, streams, groups.Count == 0, groups);
    }

    /// <summary>
    /// The pure visibility predicate. <paramref name="eventStreamIds"/> is the event's
    /// <c>event_streams</c> membership; <paramref name="eventDeviceGroupIds"/> is the set
    /// of groups the event's device belongs to (empty when the event has no device).
    /// The SQL in <c>ScopedEventReader</c> mirrors this exactly.
    /// </summary>
    public bool Allows(IReadOnlyCollection<long> eventStreamIds, IReadOnlyCollection<long> eventDeviceGroupIds)
    {
        ArgumentNullException.ThrowIfNull(eventStreamIds);
        ArgumentNullException.ThrowIfNull(eventDeviceGroupIds);

        bool streamOk = AllStreams || eventStreamIds.Any(_streamIds.Contains);
        bool groupOk = AllDeviceGroups || eventDeviceGroupIds.Any(_deviceGroupIds.Contains);
        return streamOk && groupOk;
    }
}
