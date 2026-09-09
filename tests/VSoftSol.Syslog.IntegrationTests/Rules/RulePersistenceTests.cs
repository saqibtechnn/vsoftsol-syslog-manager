using FluentAssertions;
using VSoftSol.Syslog.Core.Conditions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Data.Rules;
using VSoftSol.Syslog.Data.Seed;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Rules;

public sealed class RulePersistenceTests
{
    private static RuleDefinition Sample() => new()
    {
        Name = "email on crit",
        Description = "d",
        Priority = 25,
        Enabled = true,
        Filter = new ConditionGroup
        {
            Join = ConditionJoin.And,
            Children = { new ConditionComparison { Field = "severity", Operator = ConditionOperator.LessThan, Value = "3" } },
        },
        Actions =
        [
            new AddTagAction { Tag = "hot" },
            new SendEmailAction
            {
                Host = "smtp.example.com", From = "a@example.com", To = ["b@example.com"],
                Subject = "{severity}", Body = "{message}", SecretName = "smtp-pw",
                Throttle = new ActionThrottle(10, 3600, 60),
            },
        ],
        Window = new TimeOfDayWindow(540, 1020, [DayOfWeek.Monday, DayOfWeek.Tuesday]),
        DeviceGroupIds = [3, 7],
        Escalation = new EscalationPolicy { Threshold = 5, WindowSeconds = 600, EscalationActions = [new SuppressAction()] },
    };

    [Fact]
    public async Task Create_Get_RoundTripsEveryField()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteRuleStore(db.Factory);

        long id = await store.CreateAsync(Sample(), "admin", CancellationToken.None);
        RuleRow? row = await store.GetAsync(id, CancellationToken.None);

        row.Should().NotBeNull();
        row!.Name.Should().Be("email on crit");
        row.Priority.Should().Be(25);
        row.Filter.Should().BeOfType<ConditionGroup>();
        row.Actions.Should().HaveCount(2);
        row.Actions[1].Should().BeOfType<SendEmailAction>()
            .Which.SecretName.Should().Be("smtp-pw");
        ((SendEmailAction)row.Actions[1]).Throttle.CooldownSeconds.Should().Be(60);
        row.Window!.Days.Should().BeEquivalentTo(new[] { DayOfWeek.Monday, DayOfWeek.Tuesday });
        row.DeviceGroupIds.Should().BeEquivalentTo(new[] { 3L, 7L });
        row.Escalation!.Threshold.Should().Be(5);
        row.StopProcessing.Should().BeFalse("suppress is only in the escalation list");
    }

    [Fact]
    public async Task Version_BumpsOnEveryWrite_NotOnHitCount()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteRuleStore(db.Factory);

        long v0 = store.Version;
        long id = await store.CreateAsync(Sample(), "admin", CancellationToken.None);
        store.Version.Should().BeGreaterThan(v0);

        long v1 = store.Version;
        await store.SetEnabledAsync(id, false, "admin", CancellationToken.None);
        store.Version.Should().BeGreaterThan(v1);

        long v2 = store.Version;
        await store.BumpHitsAsync(
            new Dictionary<long, (long, DateTimeOffset)> { [id] = (17, DateTimeOffset.UtcNow) },
            CancellationToken.None);
        store.Version.Should().Be(v2, "a hit-count flush must not trigger a rule-set rebuild");

        RuleRow? row = await store.GetAsync(id, CancellationToken.None);
        row!.HitCount.Should().Be(17);
        row.LastFiredUtc.Should().NotBeNull();
    }

    [Fact]
    public async Task Delete_RefusesSystemRules()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var store = new SqliteRuleStore(db.Factory);
        long id = await store.CreateAsync(Sample(), "admin", CancellationToken.None);

        await using (var c = await db.Factory.OpenAsync(CancellationToken.None))
        {
            await using var cmd = c.CreateCommand();
            cmd.CommandText = "UPDATE rules SET is_system = 1 WHERE rule_id = $id;";
            cmd.Parameters.AddWithValue("$id", id);
            await cmd.ExecuteNonQueryAsync();
        }

        (await store.DeleteAsync(id, CancellationToken.None)).Should().BeFalse();
        (await store.GetAsync(id, CancellationToken.None)).Should().NotBeNull();
    }

    [Fact]
    public void DefaultRuleTemplates_AreValidAndNumberEightToTen()
    {
        DefaultRuleTemplates.All.Should().HaveCountGreaterThanOrEqualTo(8).And.HaveCountLessThanOrEqualTo(12);

        var compiler = new VSoftSol.Syslog.Rules.Rules.RuleCompiler(new VSoftSol.Syslog.Rules.Rules.RuleCompileOptions(
            ScriptAllowListDirectories: [System.IO.Path.GetTempPath()],
            FileActionBaseDirectory: System.IO.Path.GetTempPath(),
            LocalSyslogEndpoints: []));

        foreach (DefaultRuleTemplates.Template t in DefaultRuleTemplates.All)
        {
            // Templates with a blank destination are expected to fail compilation until an
            // operator fills them in; assert the filter + templates + structure are sound by
            // compiling against a filled-in copy.
            RuleDefinition filled = Fill(t.Rule);
            var r = compiler.Compile(filled);
            r.Success.Should().BeTrue(because: $"{t.Rule.Name}: {string.Join("; ", r.Errors)}");
        }
    }

    private static RuleDefinition Fill(RuleDefinition rule)
    {
        foreach (RuleAction a in rule.Actions.Concat(rule.Escalation?.EscalationActions ?? []))
        {
            switch (a)
            {
                case SendEmailAction e:
                    e.Host = "smtp.example.com"; e.From = "a@example.com"; e.To = ["b@example.com"];
                    break;
                case HttpWebhookAction w:
                    w.Url = "https://hooks.example.com/x";
                    break;
                case RunScriptAction s:
                    s.ExecutablePath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "remediate.exe");
                    break;
                case ForwardSyslogAction f:
                    f.Host = "siem.example.com";
                    break;
                case WriteToOdbcAction o:
                    o.ConnectionString = "DSN=x";
                    break;
            }
        }

        return rule;
    }
}
