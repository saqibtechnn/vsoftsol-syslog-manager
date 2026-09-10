using FluentAssertions;
using VSoftSol.Syslog.Core.Alerts;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Alerts;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Alerts;

public sealed class AlertPersistenceTests
{
    private static AlertDefinition Sample() => new()
    {
        Name = "burst",
        Description = "auth failures",
        Severity = NotificationLevel.Critical,
        Type = AlertEvaluationType.Threshold,
        Filter = new ConditionGroup
        {
            Join = ConditionJoin.Or,
            Children = { new ConditionComparison { Field = "message", Operator = ConditionOperator.Contains, Value = "failed" } },
        },
        WindowSeconds = 120,
        IntervalSeconds = 60,
        GroupByField = "source_ip",
        Threshold = 5,
        RemediationNotes = "check the firewall",
        Actions = [new RaiseNotificationAction { Title = "fired {hostname}" }],
        DeviceGroupIds = [3, 7],
        StreamIds = [1],
        ReNotifySeconds = 900,
        AutoResolve = true,
    };

    [Fact]
    public async Task Alert_RoundTripsThroughTheStore()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteAlertStore(db.Factory);

        long id = await store.CreateAsync(Sample(), "alice", CancellationToken.None);
        AlertRow? row = await store.GetAsync(id, CancellationToken.None);

        row.Should().NotBeNull();
        row!.Name.Should().Be("burst");
        row.Severity.Should().Be(NotificationLevel.Critical);
        row.Type.Should().Be(AlertEvaluationType.Threshold);
        row.Filter.Should().NotBeNull();
        row.GroupByField.Should().Be("source_ip");
        row.Threshold.Should().Be(5);
        row.RemediationNotes.Should().Be("check the firewall");
        row.Actions.Should().ContainSingle().Which.Should().BeOfType<RaiseNotificationAction>();
        row.DeviceGroupIds.Should().Equal(3L, 7L);
        row.StreamIds.Should().Equal(1L);
        row.ReNotifySeconds.Should().Be(900);
        row.AutoResolve.Should().BeTrue();
    }

    [Fact]
    public async Task Version_BumpsOnWrite_ButNotOnRecordFired()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteAlertStore(db.Factory);

        long v0 = store.Version;
        long id = await store.CreateAsync(Sample(), "a", CancellationToken.None);
        store.Version.Should().BeGreaterThan(v0);

        long v1 = store.Version;
        await store.RecordFiredAsync(id, DateTimeOffset.UtcNow, CancellationToken.None);
        await store.RecordEvaluatedAsync(id, DateTimeOffset.UtcNow, CancellationToken.None);
        store.Version.Should().Be(v1, "a hit / evaluation stamp must not trigger a rule-set rebuild");

        await store.SetEnabledAsync(id, false, "a", CancellationToken.None);
        store.Version.Should().BeGreaterThan(v1);
    }

    [Fact]
    public async Task SystemAlert_CannotBeDeleted_ButCanBeEdited()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteAlertStore(db.Factory);
        long id = await store.CreateAsync(Sample(), "a", CancellationToken.None);
        await Exec(db, $"UPDATE alert_definitions SET is_system = 1 WHERE alert_id = {id};");

        (await store.DeleteAsync(id, CancellationToken.None)).Should().BeFalse();

        AlertDefinition edit = Sample();
        edit.AlertId = id;
        edit.Threshold = 99;
        (await store.UpdateAsync(edit, "a", CancellationToken.None)).Should().BeTrue();
        (await store.GetAsync(id, CancellationToken.None))!.Threshold.Should().Be(99);
    }

    [Fact]
    public async Task InstanceStore_OpenIsIdempotent_PerAlertAndGroup()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        long alertId = await new SqliteAlertStore(db.Factory).CreateAsync(Sample(), "a", CancellationToken.None);
        IReadOnlyList<long> ev = await db.Repository.AppendBatchAsync(
            [SampleEvents.Minimal("a"), SampleEvents.Minimal("b"), SampleEvents.Minimal("c")], CancellationToken.None);
        var instances = new SqliteAlertInstanceStore(db.Factory);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        (long id1, bool created1) = await instances.OpenAsync(alertId, NotificationLevel.Warning, "10.0.0.1", 7, 5, [ev[0], ev[1]], now, CancellationToken.None);
        (long id2, bool created2) = await instances.OpenAsync(alertId, NotificationLevel.Warning, "10.0.0.1", 9, 5, [ev[2]], now.AddMinutes(1), CancellationToken.None);

        created1.Should().BeTrue();
        created2.Should().BeFalse("the group already has an open instance");
        id2.Should().Be(id1);

        AlertInstance? loaded = await instances.GetAsync(id1, CancellationToken.None);
        loaded!.TriggerEventIds.Should().Equal(ev[0], ev[1]);
        loaded.ObservedValue.Should().Be(7, "the observed value is from the opening breach, not the re-eval");
    }

    [Fact]
    public async Task InstanceStore_Lifecycle_RecordsTransitionsWithActorAndNote()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        long alertId = await new SqliteAlertStore(db.Factory).CreateAsync(Sample(), "a", CancellationToken.None);
        var instances = new SqliteAlertInstanceStore(db.Factory);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        (long id, _) = await instances.OpenAsync(alertId, NotificationLevel.Warning, null, 6, 5, [], now, CancellationToken.None);

        (await instances.AcknowledgeAsync(id, "bob", "looking into it", now.AddMinutes(2), CancellationToken.None)).Should().BeTrue();
        (await instances.ResolveAsync(id, "bob", "false alarm", auto: false, now.AddMinutes(5), CancellationToken.None)).Should().BeTrue();

        IReadOnlyList<AlertTransition> log = await instances.ListTransitionsAsync(id, CancellationToken.None);
        log.Select(t => t.To).Should().Equal(AlertState.Firing, AlertState.Acknowledged, AlertState.Resolved);
        log[1].Actor.Should().Be("bob");
        log[1].Note.Should().Be("looking into it");
        log[2].From.Should().Be(AlertState.Acknowledged);

        // resolving again is a no-op
        (await instances.ResolveAsync(id, "bob", null, auto: false, now.AddMinutes(6), CancellationToken.None)).Should().BeFalse();
    }

    [Fact]
    public async Task InstanceStore_AutoResolve_MarksTheAutoResolvedFlag()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        long alertId = await new SqliteAlertStore(db.Factory).CreateAsync(Sample(), "a", CancellationToken.None);
        var instances = new SqliteAlertInstanceStore(db.Factory);
        DateTimeOffset now = DateTimeOffset.UtcNow;

        (long id, _) = await instances.OpenAsync(alertId, NotificationLevel.Warning, "h", 6, 5, [], now, CancellationToken.None);
        await instances.ResolveAsync(id, "alerts-engine", "condition cleared", auto: true, now.AddMinutes(3), CancellationToken.None);

        AlertInstance? loaded = await instances.GetAsync(id, CancellationToken.None);
        loaded!.AutoResolved.Should().BeTrue();
        loaded.State.Should().Be(AlertState.Resolved);
    }

    [Fact]
    public async Task InstanceStore_CountOpenBySeverity_ExcludesResolved()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteAlertStore(db.Factory);
        long a1 = await store.CreateAsync(Sample(), "a", CancellationToken.None);
        AlertDefinition warn = Sample();
        warn.Name = "warn";
        warn.Severity = NotificationLevel.Warning;
        long a2 = await store.CreateAsync(warn, "a", CancellationToken.None);

        var instances = new SqliteAlertInstanceStore(db.Factory);
        DateTimeOffset now = DateTimeOffset.UtcNow;
        await instances.OpenAsync(a1, NotificationLevel.Critical, "x", 6, 5, [], now, CancellationToken.None);
        (long resolved, _) = await instances.OpenAsync(a2, NotificationLevel.Warning, "y", 6, 5, [], now, CancellationToken.None);
        await instances.ResolveAsync(resolved, "a", null, false, now.AddMinutes(1), CancellationToken.None);

        IReadOnlyDictionary<NotificationLevel, int> counts = await instances.CountOpenBySeverityAsync(CancellationToken.None);
        counts.GetValueOrDefault(NotificationLevel.Critical).Should().Be(1);
        counts.GetValueOrDefault(NotificationLevel.Warning).Should().Be(0);
    }

    private static async Task Exec(SqliteTestDatabase db, string sql)
    {
        await using var c = await db.Factory.OpenAsync(CancellationToken.None);
        await using var cmd = c.CreateCommand();
        cmd.CommandText = sql;
        await cmd.ExecuteNonQueryAsync();
    }
}
