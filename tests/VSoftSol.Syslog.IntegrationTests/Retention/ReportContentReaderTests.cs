using System.Text;
using FluentAssertions;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Reports;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Retention;

/// <summary>PHASE_10 — every canned/compliance template resolves against fixture data, and
/// two representative templates (one list, one aggregate) are checked against an independent
/// hand-written SQL oracle (TESTING_STANDARDS.md "assert the figures match an independent
/// SQL query").</summary>
public sealed class ReportContentReaderTests : IAsyncLifetime
{
    private RetentionTestHarness _h = null!;
    private DateTimeOffset _now;

    /// <summary>The report's "now" — one second after every fixture row (events AND audit
    /// entries alike), so nothing sits exactly on the range's exclusive upper bound.</summary>
    private DateTimeOffset _generated;

    public async Task InitializeAsync()
    {
        _h = await RetentionTestHarness.CreateAsync(seed: true); // seeded admin = user_id 1, needed for owned saved searches
        _now = _h.Clock.GetUtcNow();
        _generated = _now.AddSeconds(1);

        var events = new List<SyslogEvent>();
        string[] hosts = ["web-1", "web-1", "web-1", "db-1", "fw-1"];
        for (int i = 0; i < hosts.Length; i++)
        {
            string message = i == 0 ? "authentication failed for user bob" : $"routine message {i}";
            events.Add(new SyslogEvent
            {
                // Strictly before _now (never AT it) — the search range's ToUtc bound is
                // exclusive (SearchCompiler: "received_utc < $to"), same as every other
                // range query in the product.
                ReceivedUtc = _now.AddHours(-i - 1),
                SourceIp = "10.0.0.1",
                Hostname = hosts[i],
                Facility = Facility.Local0,
                Severity = i % 2 == 0 ? Severity.Error : Severity.Informational,
                Protocol = Protocol.Udp,
                Message = message,
                RawMessage = Encoding.UTF8.GetBytes(message),
                ParseStatus = ParseStatus.Rfc5424,
            });
        }

        await _h.SeedEventsAsync(events);

        await _h.AuditLog.AppendAsync(
            new AuditEntry(VSoftSol.Syslog.Data.Audit.AuditActions.AlertFired, Actor: "alerts-engine", EntityType: "alert", EntityId: "1", Detail: "test alert fired"),
            CancellationToken.None);
        await _h.AuditLog.AppendAsync(
            new AuditEntry(VSoftSol.Syslog.Data.Audit.AuditActions.LoginSuccess, Actor: "admin", EntityType: "session", Detail: "unrelated to rule/alert activity"),
            CancellationToken.None);
    }

    public async Task DisposeAsync() => await _h.DisposeAsync();

    [Theory]
    [InlineData(CannedReportCatalog.FailedAuthentication)]
    [InlineData(CannedReportCatalog.ConfigurationChangeAudit)]
    [InlineData(CannedReportCatalog.DeviceAvailability)]
    [InlineData(CannedReportCatalog.InterfaceFlap)]
    [InlineData(CannedReportCatalog.SeverityTrend)]
    [InlineData(CannedReportCatalog.TopTalkers)]
    [InlineData(CannedReportCatalog.RuleAlertActivity)]
    [InlineData(CannedReportCatalog.PciDss)]
    [InlineData(CannedReportCatalog.Hipaa)]
    [InlineData(CannedReportCatalog.Iso27001)]
    [InlineData(CannedReportCatalog.Sox)]
    public async Task ResolveAsync_EveryCatalogueTemplate_ResolvesWithoutError(string templateKey)
    {
        var report = new ReportDefinition { Name = "r", TemplateKey = templateKey, TimeRangeDays = 30 };

        ReportContent content = await _h.Content.ResolveAsync(report, UserScope.Unrestricted, "tester", _generated, CancellationToken.None);

        content.Should().NotBeNull();
        content.TemplateKey.Should().Be(templateKey);
    }

    [Fact]
    public async Task FailedAuthenticationSummary_MatchesAnIndependentSqlOracle()
    {
        var report = new ReportDefinition { Name = "r", TemplateKey = CannedReportCatalog.FailedAuthentication, TimeRangeDays = 30 };
        ReportContent content = await _h.Content.ResolveAsync(report, UserScope.Unrestricted, "tester", _generated, CancellationToken.None);

        long oracleCount = await ScalarAsync("SELECT COUNT(*) FROM events WHERE message LIKE '%authentication failed%';");

        content.EventRows.Should().HaveCount((int)oracleCount);
        content.EventRows.Should().OnlyContain(r => r.Message.Contains("authentication failed"));
    }

    [Fact]
    public async Task SeverityTrend_MatchesAnIndependentSqlOracle_PerSeverity()
    {
        var report = new ReportDefinition { Name = "r", TemplateKey = CannedReportCatalog.SeverityTrend, TimeRangeDays = 30 };
        ReportContent content = await _h.Content.ResolveAsync(report, UserScope.Unrestricted, "tester", _generated, CancellationToken.None);

        long errorCount = await ScalarAsync("SELECT COUNT(*) FROM events WHERE severity = 3;"); // Error = 3
        double sumForError = content.AggregateRows.Where(r => r.Group == "Error").Sum(r => r.Value);

        sumForError.Should().Be(errorCount);
    }

    [Fact]
    public async Task RuleAlertActivity_OnlyIncludesRuleActionAndAlertPrefixedAuditRows()
    {
        var report = new ReportDefinition { Name = "r", TemplateKey = CannedReportCatalog.RuleAlertActivity, TimeRangeDays = 30 };
        ReportContent content = await _h.Content.ResolveAsync(report, UserScope.Unrestricted, "tester", _generated, CancellationToken.None);

        content.AuditRows.Should().ContainSingle(r => r.Action == VSoftSol.Syslog.Data.Audit.AuditActions.AlertFired);
        content.AuditRows.Should().NotContain(r => r.Action == VSoftSol.Syslog.Data.Audit.AuditActions.LoginSuccess);
    }

    [Fact]
    public async Task CustomReport_WithAnInlineQuery_ResolvesAsAListReport()
    {
        var report = new ReportDefinition { Name = "custom", TemplateKey = ReportDefinition.CustomTemplateKey, QueryText = "host:web-1", TimeRangeDays = 30 };
        ReportContent content = await _h.Content.ResolveAsync(report, UserScope.Unrestricted, "tester", _generated, CancellationToken.None);

        content.EventRows.Should().HaveCount(3);
        content.EventRows.Should().OnlyContain(r => r.Host == "web-1");
    }

    [Fact]
    public async Task CustomReport_WithASavedSearch_ResolvesTheSavedQueryText()
    {
        long savedId = await _h.SavedSearches.CreateAsync(
            ownerUserId: 1, name: "sv", queryText: "host:db-1", timeRangeJson: null, isShared: false, CancellationToken.None);

        var report = new ReportDefinition { Name = "custom", TemplateKey = ReportDefinition.CustomTemplateKey, SavedSearchId = savedId, TimeRangeDays = 30 };
        ReportContent content = await _h.Content.ResolveAsync(report, UserScope.Unrestricted, "tester", _generated, CancellationToken.None);

        content.EventRows.Should().ContainSingle(r => r.Host == "db-1");
    }

    [Fact]
    public async Task ResolveAsync_SuccessfulReport_HasNoError()
    {
        var report = new ReportDefinition { Name = "r", TemplateKey = CannedReportCatalog.SeverityTrend, TimeRangeDays = 30 };
        ReportContent content = await _h.Content.ResolveAsync(report, UserScope.Unrestricted, "tester", _generated, CancellationToken.None);

        content.Error.Should().BeNull();
    }

    [Fact]
    public async Task CustomReport_WithAMalformedQuery_SurfacesTheParserErrorInstead_OfLookingLikeNoData()
    {
        // v1.1 — P10-2 (docs/evidence/phase-10/known-issues.md): a bare "*" is the exact
        // B10-3 mistake (a rejected wildcard-with-no-prefix, not this grammar's match-all —
        // that's an empty string). Before this fix, this silently produced a "0 rows" report
        // indistinguishable from a genuinely empty time range.
        var report = new ReportDefinition { Name = "custom", TemplateKey = ReportDefinition.CustomTemplateKey, QueryText = "*", TimeRangeDays = 30 };
        ReportContent content = await _h.Content.ResolveAsync(report, UserScope.Unrestricted, "tester", _generated, CancellationToken.None);

        content.Error.Should().NotBeNullOrEmpty();
        content.EventRows.Should().BeEmpty();
        content.RowCount.Should().Be(0);
    }

    [Fact]
    public async Task CannedAggregateReport_ScopeRestrictedButStillMatchesStreams_HasNoError()
    {
        // A scope restricted to real, matching streams is not a failure — it is the normal
        // multi-tenant case (two viewers of one shared report see their own data). Only a
        // status other than Ok should ever populate Error; this is the regression guard for
        // that boundary on the aggregate branch (see ReportContentReaderErrorMappingTests
        // for the exhaustive AggregationStatus -> Error mapping, which is what actually
        // proves P10-2 — a "scope excludes everything" UserScope cannot currently be built
        // through the public UserScope.Create/FromUser API, which always treats an empty
        // visible-set as "all", so that specific status is tested at the mapping level).
        var restricted = VSoftSol.Syslog.Core.Security.UserScope.Create(streamIds: [999_999], deviceGroupIds: null);
        var report = new ReportDefinition { Name = "r", TemplateKey = CannedReportCatalog.SeverityTrend, TimeRangeDays = 30 };

        ReportContent content = await _h.Content.ResolveAsync(report, restricted, "tester", _generated, CancellationToken.None);

        content.Error.Should().BeNull();
        content.AggregateRows.Should().BeEmpty("the restricted scope matches no real stream, but this is an empty result, not a broken query");
    }

    private async Task<long> ScalarAsync(string sql)
    {
        await using SqliteConnection connection = await _h.Db.Factory.OpenAsync(CancellationToken.None);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = sql;
        return Convert.ToInt64(await command.ExecuteScalarAsync(CancellationToken.None));
    }
}
