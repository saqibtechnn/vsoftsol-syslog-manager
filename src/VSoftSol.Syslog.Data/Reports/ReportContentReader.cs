using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Core.Reports;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Dashboards;
using VSoftSol.Syslog.Data.Retention;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Data.Search;

namespace VSoftSol.Syslog.Data.Reports;

/// <summary>
/// Resolves a <see cref="ReportDefinition"/> into <see cref="ReportContent"/> under a
/// viewer's <see cref="UserScope"/> (PHASE_10 build item 7). Reuses the Phase 5
/// <see cref="ScopedEventReader"/> for list reports and the Phase 9
/// <see cref="SqliteAggregationReader"/> for aggregate reports — the report engine adds no
/// query path of its own, so a report can never see more than the viewer's scope allows
/// (the same security property Dashboards proved in Phase 9, extended here).
/// </summary>
public sealed class ReportContentReader
{
    private readonly ScopedEventReader _events;
    private readonly SqliteAggregationReader _aggregation;
    private readonly SqliteSavedSearchStore _savedSearches;
    private readonly SqliteAuditLog _audit;
    private readonly SqliteArchiveStore _archives;
    private readonly int _maxRows;

    public ReportContentReader(
        ScopedEventReader events,
        SqliteAggregationReader aggregation,
        SqliteSavedSearchStore savedSearches,
        SqliteAuditLog audit,
        SqliteArchiveStore archives,
        int maxRows = 5_000)
    {
        _events = events ?? throw new ArgumentNullException(nameof(events));
        _aggregation = aggregation ?? throw new ArgumentNullException(nameof(aggregation));
        _savedSearches = savedSearches ?? throw new ArgumentNullException(nameof(savedSearches));
        _audit = audit ?? throw new ArgumentNullException(nameof(audit));
        _archives = archives ?? throw new ArgumentNullException(nameof(archives));
        _maxRows = maxRows;
    }

    public async Task<ReportContent> ResolveAsync(
        ReportDefinition report, UserScope scope, string generatingUser, DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        ArgumentNullException.ThrowIfNull(scope);

        DateTimeOffset to = nowUtc;
        DateTimeOffset from = to.AddDays(-Math.Max(1, report.TimeRangeDays));

        ReportTemplate? template = report.IsCustom ? null : CannedReportCatalog.Find(report.TemplateKey);
        string? controlReference = template?.ControlReference;

        string queryText;
        ReportSourceKind source;
        AggregationSpec? aggregation;
        IReadOnlyList<string> auditPrefixes;

        if (template is not null)
        {
            queryText = template.QueryText;
            source = template.Source;
            aggregation = template.Aggregation;
            auditPrefixes = template.AuditActionPrefixes;
        }
        else
        {
            source = ReportSourceKind.EventQuery;
            aggregation = null;
            auditPrefixes = [];
            queryText = report.SavedSearchId is { } savedId
                ? await _savedSearches.GetQueryTextAsync(savedId, cancellationToken).ConfigureAwait(false) ?? string.Empty
                : report.QueryText ?? string.Empty;
        }

        IReadOnlyList<ArchivedPeriod> omitted =
            await FindOmittedPeriodsAsync(scope, from, to, cancellationToken).ConfigureAwait(false);

        if (source == ReportSourceKind.AuditLog)
        {
            IReadOnlyList<ReportAuditRow> auditRows =
                await LoadAuditRowsAsync(auditPrefixes, from, to, cancellationToken).ConfigureAwait(false);
            return new ReportContent
            {
                ReportName = report.Name,
                TemplateKey = report.TemplateKey,
                ControlReference = controlReference,
                QueryText = "(audit trail: " + string.Join(", ", auditPrefixes) + ")",
                GeneratedUtc = nowUtc,
                FromUtc = from,
                ToUtc = to,
                GeneratingUser = generatingUser,
                AuditRows = auditRows,
                RowCount = auditRows.Count,
                ArchivedPeriodsOmitted = omitted,
            };
        }

        if (aggregation is not null)
        {
            SqliteAggregationReader.AggregationOutcome outcome = await _aggregation
                .AggregateAsync(scope, queryText, aggregation, from, to, aggregation.Bucket, cancellationToken).ConfigureAwait(false);

            IReadOnlyList<ReportAggregateRow> rows = MapAggregateRows(outcome.Result, aggregation.GroupByField);
            return new ReportContent
            {
                ReportName = report.Name,
                TemplateKey = report.TemplateKey,
                ControlReference = controlReference,
                QueryText = queryText,
                GeneratedUtc = nowUtc,
                FromUtc = from,
                ToUtc = to,
                GeneratingUser = generatingUser,
                AggregateRows = rows,
                RowCount = rows.Count,
                Truncated = outcome.Result.Truncated,
                ArchivedPeriodsOmitted = omitted,
            };
        }

        var request = new SearchRequest { QueryText = queryText, FromUtc = from, ToUtc = to, Limit = _maxRows };
        SearchResult result = await _events.SearchAsync(scope, request, cancellationToken).ConfigureAwait(false);
        List<ReportEventRow> eventRows = [.. result.Rows.Select(MapEventRow)];

        return new ReportContent
        {
            ReportName = report.Name,
            TemplateKey = report.TemplateKey,
            ControlReference = controlReference,
            QueryText = queryText,
            GeneratedUtc = nowUtc,
            FromUtc = from,
            ToUtc = to,
            GeneratingUser = generatingUser,
            EventRows = eventRows,
            RowCount = eventRows.Count,
            Truncated = eventRows.Count >= _maxRows,
            ArchivedPeriodsOmitted = omitted,
        };
    }

    private async Task<IReadOnlyList<ArchivedPeriod>> FindOmittedPeriodsAsync(
        UserScope scope, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        IReadOnlyCollection<long>? streamScope = scope.AllStreams ? null : scope.StreamIds;
        IReadOnlyList<Core.Retention.ArchiveRecord> archives =
            await _archives.ListAsync(streamScope, cancellationToken).ConfigureAwait(false);

        return [.. archives
            .Where(a => a.PeriodStartUtc < to && a.PeriodEndUtc > from)
            .OrderBy(a => a.PeriodStartUtc)
            .Take(20)
            .Select(a => new ArchivedPeriod(a.PeriodStartUtc, a.PeriodEndUtc))];
    }

    private async Task<IReadOnlyList<ReportAuditRow>> LoadAuditRowsAsync(
        IReadOnlyList<string> prefixes, DateTimeOffset from, DateTimeOffset to, CancellationToken cancellationToken)
    {
        IReadOnlyList<AuditRecord> records = await _audit
            .QueryAsync(new AuditQuery { FromUtc = from, ToUtc = to, Limit = 5_000 }, cancellationToken).ConfigureAwait(false);

        return [.. records
            .Where(r => prefixes.Count == 0 || prefixes.Any(p => r.Action.StartsWith(p, StringComparison.Ordinal)))
            .Select(r => new ReportAuditRow(r.OccurredUtc, r.Actor, r.Action, r.EntityType, r.EntityId, r.Detail))];
    }

    private static IReadOnlyList<ReportAggregateRow> MapAggregateRows(AggregationResult result, string? groupByField)
    {
        Func<string?, string> display = groupByField == "severity" ? AggregationConstants.DisplaySeverity : AggregationConstants.DisplayGroup;
        return [.. result.Points
            .OrderBy(p => p.GroupKey, StringComparer.Ordinal)
            .ThenBy(p => p.BucketIndex)
            .Select(p => new ReportAggregateRow(
                display(p.GroupKey),
                p.BucketIndex is { } idx && result.Plan is { } plan ? FormatBucket(plan, idx) : null,
                p.Value))];
    }

    private static string FormatBucket(BucketPlan plan, int index) =>
        plan.BucketStartUtc(index).UtcDateTime.ToString("yyyy-MM-dd HH:mm", System.Globalization.CultureInfo.InvariantCulture) + " UTC";

    private static ReportEventRow MapEventRow(SyslogEvent e) =>
        new(e.EventId, e.ReceivedUtc, e.Severity.ToString(), e.Hostname, e.AppName, e.Message, e.Vendor);
}
