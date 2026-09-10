using System.Globalization;
using Microsoft.Data.Sqlite;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Core.Search;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.Data.Search;
using VSoftSol.Syslog.Data.Sqlite;

namespace VSoftSol.Syslog.Data.Dashboards;

/// <summary>
/// Runs a widget's aggregation over <c>events</c> under a principal's
/// <see cref="UserScope"/> (PHASE_09 build item 2 + item 7). The query text is parsed with
/// the Phase 5 parser and compiled with the Phase 5 <see cref="SearchCompiler"/>, so the
/// scope clauses are part of the <c>WHERE</c> — two viewers of one shared dashboard see
/// their own data, and an aggregate never spans out-of-scope events. Every value is a bound
/// parameter.
/// </summary>
public sealed class SqliteAggregationReader
{
    private readonly SqliteConnectionFactory _factory;

    public SqliteAggregationReader(SqliteConnectionFactory factory) =>
        _factory = factory ?? throw new ArgumentNullException(nameof(factory));

    /// <summary>The parsed / compiled outcome, so a widget can show why it is empty.</summary>
    public enum AggregationStatus
    {
        Ok,
        BadQuery,
        BadAggregation,
        ScopeExcludesEverything,
    }

    public sealed record AggregationOutcome(AggregationStatus Status, AggregationResult Result, string? Detail)
    {
        public static AggregationOutcome Ok(AggregationResult result) => new(AggregationStatus.Ok, result, null);
    }

    public async Task<AggregationOutcome> AggregateAsync(
        UserScope scope,
        string queryText,
        AggregationSpec spec,
        DateTimeOffset fromUtc,
        DateTimeOffset toUtc,
        BucketInterval bucket,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(spec);

        SearchParseResult parsed = SearchQueryParser.Parse(queryText ?? string.Empty);
        if (!parsed.Success)
        {
            return new AggregationOutcome(AggregationStatus.BadQuery, AggregationResult.Empty, parsed.Error);
        }

        var request = new SearchRequest { QueryText = queryText ?? string.Empty, FromUtc = fromUtc, ToUtc = toUtc };
        CompiledSearch predicate = new SearchCompiler().Compile(request, parsed.Query!, scope);

        BucketPlan? plan = bucket == BucketInterval.None ? null : TimeBucketing.Plan(fromUtc, toUtc, bucket);
        CompiledAggregation compiled = new AggregationCompiler().Compile(spec, predicate, plan);
        if (compiled.Rejected is not null)
        {
            AggregationStatus status = predicate.MatchesNothing
                ? AggregationStatus.ScopeExcludesEverything
                : AggregationStatus.BadAggregation;
            return new AggregationOutcome(
                status,
                status == AggregationStatus.ScopeExcludesEverything ? WithPlan(AggregationResult.Empty, plan) : AggregationResult.Empty,
                compiled.Rejected);
        }

        var raw = new List<AggPoint>();
        await using (SqliteConnection connection = await _factory.OpenAsync(cancellationToken).ConfigureAwait(false))
        await using (SqliteCommand command = connection.CreateCommand())
        {
            command.CommandText = compiled.Sql;
            foreach (SearchParameter p in compiled.Parameters)
            {
                command.Parameters.AddWithValue(p.Name, p.Value);
            }

            await using SqliteDataReader reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                string? group = compiled.Grouped ? reader.GetString(0) : null;
                int? bucketIndex = compiled.Bucketed ? (int?)reader.GetInt64(1) : null;
                double value = reader.IsDBNull(2) ? 0 : reader.GetDouble(2);
                raw.Add(new AggPoint(group, bucketIndex, value));
            }
        }

        (IReadOnlyList<AggPoint> points, int omitted) = compiled.Grouped
            ? ApplyTopN(raw, Math.Clamp(spec.TopN, 1, WidgetValidator.MaxTopN))
            : (raw, 0);

        return AggregationOutcome.Ok(new AggregationResult
        {
            Points = DropOutOfRangeBuckets(points, plan),
            Plan = plan,
            Truncated = false,
            GroupsOmitted = omitted,
        });
    }

    private static AggregationResult WithPlan(AggregationResult empty, BucketPlan? plan) =>
        plan is null ? empty : empty with { Plan = plan };

    private static IReadOnlyList<AggPoint> DropOutOfRangeBuckets(IReadOnlyList<AggPoint> points, BucketPlan? plan)
    {
        if (plan is null)
        {
            return points;
        }

        // A late event at exactly `to` (or a clock skew) can land one bucket past the plan;
        // fold it into the last bucket rather than dropping the count.
        var fixedUp = new List<AggPoint>(points.Count);
        foreach (AggPoint p in points)
        {
            if (p.BucketIndex is not { } i)
            {
                fixedUp.Add(p);
            }
            else if (i < 0)
            {
                fixedUp.Add(p with { BucketIndex = 0 });
            }
            else if (i >= plan.Count)
            {
                fixedUp.Add(p with { BucketIndex = plan.Count - 1 });
            }
            else
            {
                fixedUp.Add(p);
            }
        }

        return fixedUp;
    }

    private static (IReadOnlyList<AggPoint> Points, int Omitted) ApplyTopN(IReadOnlyList<AggPoint> points, int topN)
    {
        var totals = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (AggPoint p in points)
        {
            string key = p.GroupKey ?? AggregationConstants.NoneKey;
            totals[key] = totals.GetValueOrDefault(key) + p.Value;
        }

        if (totals.Count <= topN)
        {
            return (points, 0);
        }

        var keep = totals
            .OrderByDescending(kv => kv.Value)
            .ThenBy(kv => kv.Key, StringComparer.Ordinal)
            .Take(topN)
            .Select(kv => kv.Key)
            .ToHashSet(StringComparer.Ordinal);

        var kept = points.Where(p => keep.Contains(p.GroupKey ?? AggregationConstants.NoneKey)).ToList();
        return (kept, totals.Count - topN);
    }
}

/// <summary>Shared constants for the aggregation layer.</summary>
public static class AggregationConstants
{
    /// <summary>The label SQL <c>COALESCE</c> maps a null group column to.</summary>
    public const string NoneKey = "(none)";

    /// <summary>Formats a group key for display, mapping the SQL sentinel to something readable.</summary>
    public static string DisplayGroup(string? key) =>
        key is null or NoneKey ? "(none)" : key;

    /// <summary>Formats a severity-code group key (0-7) as its name, or passes other keys through.</summary>
    public static string DisplaySeverity(string? key) =>
        int.TryParse(key, NumberStyles.Integer, CultureInfo.InvariantCulture, out int code) && code is >= 0 and <= 7
            ? ((VSoftSol.Syslog.Core.Enums.Severity)code).ToString()
            : DisplayGroup(key);
}
