namespace VSoftSol.Syslog.Core.Devices;

/// <summary>Where a device sits in the discovery lifecycle (PHASE_06 build item 2).</summary>
public enum DeviceApprovalStatus
{
    /// <summary>Manually created, or a discovered device an admin approved.</summary>
    Approved,

    /// <summary>Auto-discovered from an unknown source IP; waiting in the approval queue.</summary>
    Pending,

    /// <summary>An admin rejected it. The IP mapping is kept so the same source is not re-queued.</summary>
    Rejected,
}

/// <summary>What to do with a message from a source IP that matches no known device.</summary>
public enum UnknownSourcePolicy
{
    /// <summary>Create a pending device record and queue it for approval.</summary>
    AutoRegister,

    /// <summary>Accept the message but leave it unattributed (no device link, no queue entry).</summary>
    AsUnknown,

    /// <summary>Drop the message. (Rare — for locked-down installs.)</summary>
    Reject,
}

/// <summary>A registered (or pending) device (PHASE_06 build item 1).</summary>
public sealed record Device
{
    public required long DeviceId { get; set; }

    public required string Name { get; set; }

    public string? Hostname { get; set; }

    /// <summary>The primary source IP. Every IP the device sends from is in <see cref="Ips"/>.</summary>
    public string? PrimaryIp { get; set; }

    public IReadOnlyList<string> Ips { get; set; } = [];

    public string? Vendor { get; set; }

    public string? Model { get; set; }

    public string? Role { get; set; }

    public string? Site { get; set; }

    public string? Owner { get; set; }

    public string? Notes { get; set; }

    /// <summary>IANA timezone for interpreting ambiguous timestamps from this device (VENDOR_SUPPORT.md).</summary>
    public string? Timezone { get; set; }

    /// <summary>Expected messages per minute; null = no expectation. Backs the health card.</summary>
    public int? ExpectedMessageRate { get; set; }

    /// <summary>Alert if the device is silent for this many minutes (Phase 8 consumes it).</summary>
    public int? HeartbeatMinutes { get; set; }

    public bool IsEnabled { get; set; } = true;

    /// <summary>True when the record was created by auto-discovery rather than by hand.</summary>
    public bool Discovered { get; set; }

    public DeviceApprovalStatus ApprovalStatus { get; set; } = DeviceApprovalStatus.Approved;

    public DateTimeOffset? FirstSeenUtc { get; set; }

    public DateTimeOffset? LastSeenUtc { get; set; }

    public DateTimeOffset CreatedUtc { get; set; }

    public IReadOnlyList<long> GroupIds { get; set; } = [];
}

/// <summary>A device group — used for RBAC scoping, filtering, dashboards, and bulk rules.</summary>
public sealed record DeviceGroup
{
    public required long GroupId { get; set; }

    public required string Name { get; set; }

    public string? Description { get; set; }

    public int MemberCount { get; set; }
}

/// <summary>The single-row discovery policy.</summary>
public sealed record DiscoverySettings
{
    public UnknownSourcePolicy UnknownSourcePolicy { get; set; } = UnknownSourcePolicy.AutoRegister;

    public int MaxPendingDevices { get; set; } = 500;
}
