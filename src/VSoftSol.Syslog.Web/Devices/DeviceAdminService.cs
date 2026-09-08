using VSoftSol.Syslog.Core.Devices;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Devices;
using VSoftSol.Syslog.Web.Security;

namespace VSoftSol.Syslog.Web.Devices;

/// <summary>The outcome of a device admin operation.</summary>
public sealed record DeviceActionResult(bool Ok, string Message)
{
    public static DeviceActionResult Success(string message) => new(true, message);

    public static DeviceActionResult Fail(string message) => new(false, message);
}

/// <summary>
/// Device / group / discovery mutations from the UI. Approval and rejection are checked
/// against the caller's role <b>here</b>, not only by the page's <c>[Authorize]</c>
/// (PHASE_06 security: "assert Operator and Read-Only are refused at the API"). Every
/// mutation writes the audit log and invalidates the ingest-path device cache.
/// </summary>
public sealed class DeviceAdminService
{
    private readonly SqliteDeviceStore _devices;
    private readonly SqliteDeviceGroupStore _groups;
    private readonly SqliteDiscoverySettingsStore _discovery;
    private readonly DeviceResolver _resolver;
    private readonly SqliteAuditLog _audit;
    private readonly CurrentUserAccessor _users;

    public DeviceAdminService(
        SqliteDeviceStore devices,
        SqliteDeviceGroupStore groups,
        SqliteDiscoverySettingsStore discovery,
        DeviceResolver resolver,
        SqliteAuditLog audit,
        CurrentUserAccessor users)
    {
        _devices = devices;
        _groups = groups;
        _discovery = discovery;
        _resolver = resolver;
        _audit = audit;
        _users = users;
    }

    public Task<IReadOnlyList<Device>> ListAsync(CancellationToken ct) =>
        _devices.ListAsync([DeviceApprovalStatus.Approved], ct);

    public Task<IReadOnlyList<Device>> ListPendingAsync(CancellationToken ct) =>
        _devices.ListAsync([DeviceApprovalStatus.Pending], ct);

    public Task<long> CountPendingAsync(CancellationToken ct) => _devices.CountPendingAsync(ct);

    public Task<Device?> GetAsync(long id, CancellationToken ct) => _devices.GetAsync(id, ct);

    public Task<IReadOnlyList<DeviceGroup>> ListGroupsAsync(CancellationToken ct) => _groups.ListAsync(ct);

    public Task<DiscoverySettings> GetDiscoverySettingsAsync(CancellationToken ct) => _discovery.GetAsync(ct);

    public async Task<DeviceActionResult> SaveAsync(Device device, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role is not (Role.Administrator or Role.Operator))
        {
            return DeviceActionResult.Fail("You do not have permission to change devices.");
        }

        bool isNew = device.DeviceId == 0;
        if (isNew)
        {
            long id = await _devices.CreateAsync(device, ct).ConfigureAwait(false);
            await Audit(AuditActions.DeviceCreate, id, device.Name, user).ConfigureAwait(false);
        }
        else
        {
            if (!await _devices.UpdateAsync(device, ct).ConfigureAwait(false))
            {
                return DeviceActionResult.Fail("That device no longer exists.");
            }

            await Audit(AuditActions.DeviceUpdate, device.DeviceId, device.Name, user).ConfigureAwait(false);
        }

        _resolver.Invalidate();
        return DeviceActionResult.Success(isNew ? "Device added." : "Device saved.");
    }

    public async Task<DeviceActionResult> ApproveAsync(
        long deviceId, string name, string? vendor, string? role, int? heartbeatMinutes,
        IReadOnlyCollection<long> groupIds, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role != Role.Administrator)
        {
            return DeviceActionResult.Fail("Only an administrator can approve a discovered device.");
        }

        if (string.IsNullOrWhiteSpace(name))
        {
            return DeviceActionResult.Fail("Give the device a name before approving it.");
        }

        bool ok = await _devices.ApproveAsync(deviceId, name, vendor, role, heartbeatMinutes, groupIds, user.UserName, ct)
            .ConfigureAwait(false);
        if (!ok)
        {
            return DeviceActionResult.Fail("That device is no longer pending.");
        }

        await Audit(AuditActions.DeviceApprove, deviceId, name, user).ConfigureAwait(false);
        _resolver.Invalidate();
        return DeviceActionResult.Success($"Approved “{name}”.");
    }

    public async Task<DeviceActionResult> RejectAsync(long deviceId, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role != Role.Administrator)
        {
            return DeviceActionResult.Fail("Only an administrator can reject a discovered device.");
        }

        bool ok = await _devices.RejectAsync(deviceId, user.UserName, ct).ConfigureAwait(false);
        if (!ok)
        {
            return DeviceActionResult.Fail("That device is no longer pending.");
        }

        await Audit(AuditActions.DeviceReject, deviceId, "#" + deviceId, user).ConfigureAwait(false);
        _resolver.Invalidate();
        return DeviceActionResult.Success("Device rejected.");
    }

    public async Task<DeviceActionResult> SaveGroupAsync(
        long groupId, string name, string? description, IReadOnlyCollection<long> memberIds, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role is not (Role.Administrator or Role.Operator))
        {
            return DeviceActionResult.Fail("You do not have permission to change device groups.");
        }

        long id = groupId;
        if (groupId == 0)
        {
            id = await _groups.CreateAsync(name, description, ct).ConfigureAwait(false);
        }
        else if (!await _groups.RenameAsync(groupId, name, description, ct).ConfigureAwait(false))
        {
            return DeviceActionResult.Fail("That group no longer exists.");
        }

        await _groups.SetMembersAsync(id, memberIds, ct).ConfigureAwait(false);
        await Audit(AuditActions.DeviceGroupChange, id, name, user).ConfigureAwait(false);
        _resolver.Invalidate();
        return DeviceActionResult.Success("Group saved.");
    }

    public async Task<DeviceActionResult> SaveDiscoverySettingsAsync(DiscoverySettings settings, CancellationToken ct)
    {
        CurrentUser user = await _users.GetAsync().ConfigureAwait(false);
        if (user.Role != Role.Administrator)
        {
            return DeviceActionResult.Fail("Only an administrator can change discovery settings.");
        }

        await _discovery.SetAsync(settings, user.UserName, ct).ConfigureAwait(false);
        await _audit.AppendAsync(
            new AuditEntry(AuditActions.ConfigChange, user.UserName, "discovery_settings",
                Detail: $"policy={settings.UnknownSourcePolicy}; maxPending={settings.MaxPendingDevices}"),
            CancellationToken.None).ConfigureAwait(false);
        _resolver.Invalidate();
        return DeviceActionResult.Success("Discovery settings saved.");
    }

    private Task Audit(string action, long deviceId, string name, CurrentUser user) =>
        _audit.AppendAsync(
            new AuditEntry(action, user.UserName, "device", deviceId.ToString(System.Globalization.CultureInfo.InvariantCulture),
                Detail: name),
            CancellationToken.None);
}
