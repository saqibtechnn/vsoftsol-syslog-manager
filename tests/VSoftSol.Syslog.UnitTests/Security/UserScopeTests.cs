using FluentAssertions;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Security;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Security;

/// <summary>
/// The pure visibility predicate behind the scope chokepoint (PHASE_04 build item 4).
/// The SQL in <c>ScopedEventReader</c> must match this exactly.
/// </summary>
public sealed class UserScopeTests
{
    [Fact]
    public void Unrestricted_AllowsAnyEvent()
    {
        UserScope.Unrestricted.Allows([], []).Should().BeTrue();
        UserScope.Unrestricted.Allows([99], [99]).Should().BeTrue();
        UserScope.Unrestricted.IsUnrestricted.Should().BeTrue();
    }

    [Fact]
    public void FromUser_WithEmptySets_IsUnrestricted()
    {
        var user = new AuthenticatedUser("u", "U", Role.Administrator, [], [], false);

        UserScope.FromUser(user).IsUnrestricted.Should().BeTrue();
    }

    [Fact]
    public void StreamRestricted_AllowsEventInAVisibleStream_DeniesOthers()
    {
        UserScope scope = UserScope.Create(streamIds: [10, 20], deviceGroupIds: null);

        scope.IsUnrestricted.Should().BeFalse();
        scope.Allows(eventStreamIds: [20], eventDeviceGroupIds: []).Should().BeTrue();
        scope.Allows(eventStreamIds: [30], eventDeviceGroupIds: []).Should().BeFalse();
        scope.Allows(eventStreamIds: [], eventDeviceGroupIds: []).Should().BeFalse("an event with no stream is invisible to a stream-restricted user");
    }

    [Fact]
    public void DeviceGroupRestricted_AllowsEventFromADeviceInAVisibleGroup_DeniesOthers()
    {
        UserScope scope = UserScope.Create(streamIds: null, deviceGroupIds: [5]);

        scope.Allows(eventStreamIds: [1], eventDeviceGroupIds: [5]).Should().BeTrue();
        scope.Allows(eventStreamIds: [1], eventDeviceGroupIds: [6]).Should().BeFalse();
        scope.Allows(eventStreamIds: [1], eventDeviceGroupIds: []).Should().BeFalse("an event with no device is invisible to a group-restricted user");
    }

    [Fact]
    public void RestrictedOnBothDimensions_RequiresBoth()
    {
        UserScope scope = UserScope.Create(streamIds: [10], deviceGroupIds: [5]);

        scope.Allows(eventStreamIds: [10], eventDeviceGroupIds: [5]).Should().BeTrue();
        scope.Allows(eventStreamIds: [10], eventDeviceGroupIds: [6]).Should().BeFalse();
        scope.Allows(eventStreamIds: [11], eventDeviceGroupIds: [5]).Should().BeFalse();
    }
}
