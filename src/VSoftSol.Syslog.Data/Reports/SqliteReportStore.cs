using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Reports;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Reports;

/// <summary>CRUD for <see cref="ReportDefinition"/> and <see cref="ReportRunSummary"/>
/// (PHASE_10 build items 6/8). A system (canned/compliance) report is visible to everyone
/// with report access and never editable or deletable — the same IDOR discipline as
/// <c>Dashboards.SqliteDashboardStore</c>.</summary>
public sealed class SqliteReportStore
{
    private const string Columns =
        "report_id, name, template_key, saved_search_id, query_text, time_range_days, schedule, " +
        "delivery_json, owner_user_id, is_system, enabled, created_utc, updated_utc, updated_by, last_run_utc, next_run_utc";

    private readonly SqliteConnectionFactory _factory;
    private readonly TimeProvider _time;

    public SqliteReportStore(SqliteConnectionFactory factory, TimeProvider? timeProvider = null)
    {
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));
        _time = timeProvider ?? TimeProvider.System;
    }

    public async Task<IReadOnlyList<ReportDefinition>> ListForUserAsync(long userId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM reports WHERE is_system = 1 OR owner_user_id = $uid ORDER BY is_system DESC, name COLLATE NOCASE;";
        command.Parameters.AddWithValue("$uid", userId);

        var result = new List<ReportDefinition>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public async Task<ReportDefinition?> GetAsync(long id, long requestingUserId, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM reports WHERE report_id = $id AND (is_system = 1 OR owner_user_id = $uid);";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$uid", requestingUserId);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    public async Task<ReportDefinition?> GetByTemplateKeyAsync(string templateKey, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"SELECT {Columns} FROM reports WHERE template_key = $key AND is_system = 1;";
        command.Parameters.AddWithValue("$key", templateKey);

        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        return await reader.ReadAsync(cancellationToken).ConfigureAwait(false) ? Map(reader) : null;
    }

    /// <summary>Reports due to run now — enabled, scheduled, and past their <c>next_run_utc</c>.</summary>
    public async Task<IReadOnlyList<ReportDefinition>> ListDueAsync(DateTimeOffset nowUtc, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = $"""
            SELECT {Columns} FROM reports
            WHERE enabled = 1 AND schedule <> 'none' AND next_run_utc IS NOT NULL AND next_run_utc <= $now;
            """;
        command.Parameters.AddWithValue("$now", StorageFormat.Timestamp(nowUtc));

        var result = new List<ReportDefinition>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(Map(reader));
        }

        return result;
    }

    public async Task<long> CreateAsync(ReportDefinition report, long? ownerUserId, bool isSystem, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO reports
                (name, template_key, saved_search_id, query_text, time_range_days, schedule, delivery_json,
                 owner_user_id, is_system, enabled, created_utc, updated_utc, updated_by, next_run_utc)
            VALUES
                ($name, $key, $search, $query, $range, $schedule, $delivery, $owner, $system, $enabled, $now, $now, $by, $next);
            SELECT last_insert_rowid();
            """;
        BindBody(command, report);
        command.Parameters.AddWithValue("$owner", (object?)ownerUserId ?? DBNull.Value);
        command.Parameters.AddWithValue("$system", isSystem ? 1 : 0);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);
        command.Parameters.AddWithValue(
            "$next", report.Schedule.NextRunUtc(_time.GetUtcNow()) is { } next ? StorageFormat.Timestamp(next) : (object)DBNull.Value);

        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task<bool> UpdateAsync(ReportDefinition report, long requestingUserId, string updatedBy, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(report);
        string now = StorageFormat.Timestamp(_time.GetUtcNow());

        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE reports SET
                name = $name, template_key = $key, saved_search_id = $search, query_text = $query,
                time_range_days = $range, schedule = $schedule, delivery_json = $delivery,
                enabled = $enabled, updated_utc = $now, updated_by = $by, next_run_utc = $next
            WHERE report_id = $id AND owner_user_id = $uid AND is_system = 0;
            """;
        BindBody(command, report);
        command.Parameters.AddWithValue("$id", report.Id);
        command.Parameters.AddWithValue("$uid", requestingUserId);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", updatedBy);
        command.Parameters.AddWithValue(
            "$next", report.Schedule.NextRunUtc(_time.GetUtcNow()) is { } next ? StorageFormat.Timestamp(next) : (object)DBNull.Value);

        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    public async Task<bool> DeleteAsync(long id, long requestingUserId, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "DELETE FROM reports WHERE report_id = $id AND owner_user_id = $uid AND is_system = 0;";
        command.Parameters.AddWithValue("$id", id);
        command.Parameters.AddWithValue("$uid", requestingUserId);
        return await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false) == 1;
    }

    /// <summary>Upserts a system report by template key — used only by the seeder.</summary>
    public async Task UpsertSystemReportAsync(ReportDefinition report, CancellationToken cancellationToken)
    {
        ReportDefinition? existing = await GetByTemplateKeyAsync(report.TemplateKey, cancellationToken).ConfigureAwait(false);
        if (existing is null)
        {
            await CreateAsync(report, ownerUserId: null, isSystem: true, updatedBy: "seed", cancellationToken).ConfigureAwait(false);
            return;
        }

        string now = StorageFormat.Timestamp(_time.GetUtcNow());
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE reports SET name = $name, time_range_days = $range, updated_utc = $now WHERE report_id = $id;";
        command.Parameters.AddWithValue("$name", report.Name);
        command.Parameters.AddWithValue("$range", report.TimeRangeDays);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$id", existing.Id);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    // ------------------------------------------------------------- runs

    public async Task<long> StartRunAsync(long reportId, string triggeredBy, CancellationToken cancellationToken)
    {
        string now = StorageFormat.Timestamp(_time.GetUtcNow());
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            INSERT INTO report_runs (report_id, started_utc, status, triggered_by) VALUES ($rid, $now, 'running', $by);
            SELECT last_insert_rowid();
            """;
        command.Parameters.AddWithValue("$rid", reportId);
        command.Parameters.AddWithValue("$now", now);
        command.Parameters.AddWithValue("$by", triggeredBy);
        return Convert.ToInt64(await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false), CultureInfo.InvariantCulture);
    }

    public async Task CompleteRunAsync(
        long runId, ReportRunStatus status, int? rowCount, string? pdfPath, string? csvPath, string? error, CancellationToken cancellationToken)
    {
        DateTimeOffset now = _time.GetUtcNow();
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            UPDATE report_runs SET completed_utc = $now, status = $status, row_count = $rows,
                pdf_path = $pdf, csv_path = $csv, error = $error
            WHERE run_id = $id;
            """;
        command.Parameters.AddWithValue("$now", StorageFormat.Timestamp(now));
        command.Parameters.AddWithValue("$status", status switch { ReportRunStatus.Ok => "ok", ReportRunStatus.Failed => "failed", _ => "running" });
        command.Parameters.AddWithValue("$rows", (object?)rowCount ?? DBNull.Value);
        command.Parameters.AddWithValue("$pdf", (object?)pdfPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$csv", (object?)csvPath ?? DBNull.Value);
        command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        command.Parameters.AddWithValue("$id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);

        await using SqliteCommand touch = connection.CreateCommand();
        touch.CommandText = "UPDATE reports SET last_run_utc = $now WHERE report_id = (SELECT report_id FROM report_runs WHERE run_id = $id);";
        touch.Parameters.AddWithValue("$now", StorageFormat.Timestamp(now));
        touch.Parameters.AddWithValue("$id", runId);
        await touch.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task RecordDeliveryAsync(long runId, bool ok, string? error, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = ok
            ? "UPDATE report_runs SET delivered_utc = $now, delivery_error = NULL WHERE run_id = $id;"
            : "UPDATE report_runs SET delivery_error = $error WHERE run_id = $id;";
        if (ok)
        {
            command.Parameters.AddWithValue("$now", StorageFormat.Timestamp(_time.GetUtcNow()));
        }
        else
        {
            command.Parameters.AddWithValue("$error", (object?)error ?? DBNull.Value);
        }

        command.Parameters.AddWithValue("$id", runId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task AdvanceNextRunAsync(long reportId, DateTimeOffset? nextRunUtc, CancellationToken cancellationToken)
    {
        await using IAsyncDisposable writeLock = await _factory.AcquireWriteLockAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = "UPDATE reports SET next_run_utc = $next WHERE report_id = $id;";
        command.Parameters.AddWithValue("$next", nextRunUtc is { } n ? StorageFormat.Timestamp(n) : (object)DBNull.Value);
        command.Parameters.AddWithValue("$id", reportId);
        await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
    }

    public async Task<IReadOnlyList<ReportRunSummary>> ListRunsAsync(long reportId, int limit, CancellationToken cancellationToken)
    {
        await using SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false);
        await using SqliteCommand command = connection.CreateCommand();
        command.CommandText = """
            SELECT run_id, report_id, started_utc, completed_utc, status, row_count, pdf_path, csv_path,
                   error, delivered_utc, delivery_error, triggered_by
            FROM report_runs WHERE report_id = $id ORDER BY started_utc DESC LIMIT $limit;
            """;
        command.Parameters.AddWithValue("$id", reportId);
        command.Parameters.AddWithValue("$limit", limit);

        var result = new List<ReportRunSummary>();
        await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            result.Add(new ReportRunSummary
            {
                RunId = reader.GetInt64(0),
                ReportId = reader.GetInt64(1),
                StartedUtc = StorageFormat.ParseTimestamp(reader.GetString(2)),
                CompletedUtc = reader.IsDBNull(3) ? null : StorageFormat.ParseTimestamp(reader.GetString(3)),
                Status = reader.GetString(4) switch { "ok" => ReportRunStatus.Ok, "failed" => ReportRunStatus.Failed, _ => ReportRunStatus.Running },
                RowCount = reader.IsDBNull(5) ? null : reader.GetInt32(5),
                PdfPath = reader.IsDBNull(6) ? null : reader.GetString(6),
                CsvPath = reader.IsDBNull(7) ? null : reader.GetString(7),
                Error = reader.IsDBNull(8) ? null : reader.GetString(8),
                DeliveredUtc = reader.IsDBNull(9) ? null : StorageFormat.ParseTimestamp(reader.GetString(9)),
                DeliveryError = reader.IsDBNull(10) ? null : reader.GetString(10),
                TriggeredBy = reader.GetString(11),
            });
        }

        return result;
    }

    private static void BindBody(SqliteCommand command, ReportDefinition report)
    {
        command.Parameters.AddWithValue("$name", report.Name.Trim());
        command.Parameters.AddWithValue("$key", report.TemplateKey);
        command.Parameters.AddWithValue("$search", (object?)report.SavedSearchId ?? DBNull.Value);
        command.Parameters.AddWithValue("$query", (object?)report.QueryText ?? DBNull.Value);
        command.Parameters.AddWithValue("$range", report.TimeRangeDays);
        command.Parameters.AddWithValue("$schedule", report.Schedule.ToString().ToLowerInvariant());
        command.Parameters.AddWithValue("$delivery", ReportJson.SerializeDelivery(report.Delivery));
        command.Parameters.AddWithValue("$enabled", report.Enabled ? 1 : 0);
    }

    private static ReportDefinition Map(SqliteDataReader reader) => new()
    {
        Id = reader.GetInt64(0),
        Name = reader.GetString(1),
        TemplateKey = reader.GetString(2),
        SavedSearchId = reader.IsDBNull(3) ? null : reader.GetInt64(3),
        QueryText = reader.IsDBNull(4) ? null : reader.GetString(4),
        TimeRangeDays = reader.GetInt32(5),
        Schedule = reader.GetString(6) switch
        {
            "daily" => ReportSchedule.Daily,
            "weekly" => ReportSchedule.Weekly,
            "monthly" => ReportSchedule.Monthly,
            _ => ReportSchedule.None,
        },
        Delivery = ReportJson.DeserializeDelivery(reader.GetString(7)),
        OwnerUserId = reader.IsDBNull(8) ? null : reader.GetInt64(8),
        IsSystem = reader.GetInt64(9) == 1,
        Enabled = reader.GetInt64(10) == 1,
        CreatedUtc = StorageFormat.ParseTimestamp(reader.GetString(11)),
        UpdatedUtc = reader.IsDBNull(12) ? null : StorageFormat.ParseTimestamp(reader.GetString(12)),
        UpdatedBy = reader.IsDBNull(13) ? null : reader.GetString(13),
        LastRunUtc = reader.IsDBNull(14) ? null : StorageFormat.ParseTimestamp(reader.GetString(14)),
        NextRunUtc = reader.IsDBNull(15) ? null : StorageFormat.ParseTimestamp(reader.GetString(15)),
    };
}
