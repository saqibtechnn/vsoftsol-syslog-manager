using System.Globalization;
using VSoftSol.Syslog.Core.Dashboards;
using VSoftSol.Syslog.Data.Search;

namespace VSoftSol.Syslog.Data.Dashboards;

/// <summary>
/// Turns an <see cref="AggregationSpec"/> plus an already-scoped, already-parameterised
/// search predicate (<see cref="CompiledSearch"/> from <see cref="SearchCompiler"/>) into a
/// single <c>SELECT … GROUP BY</c>. Because the predicate comes straight from the search
/// compiler, the principal's scope is baked into the <c>WHERE</c> — an aggregate can never
/// count an out-of-scope event (PHASE_09 security: "an aggregate that includes out-of-scope
/// events is a data leak even when no individual event is displayed").
/// </summary>
internal sealed class AggregationCompiler
{
    private readonly List<SearchParameter> _extra = [];

    public CompiledAggregation Compile(AggregationSpec spec, CompiledSearch predicate, BucketPlan? plan)
    {
        ArgumentNullException.ThrowIfNull(spec);
        ArgumentNullException.ThrowIfNull(predicate);

        if (predicate.MatchesNothing)
        {
            return CompiledAggregation.Reject("scope excludes everything");
        }

        // Group-by (a severity donut always groups by severity).
        string? groupBy = spec.GroupByField;
        string groupExpr = "''";
        bool grouped = false;
        if (groupBy is not null)
        {
            if (!EventColumns.TryResolveGroup(groupBy, out string? column, out string? extractedName))
            {
                return CompiledAggregation.Reject($"'{groupBy}' cannot be grouped");
            }

            groupExpr = column is not null
                ? $"CAST(COALESCE(e.{column}, '(none)') AS TEXT)"
                : $"CAST(COALESCE({ExtractedScalar("g", extractedName!)}, '(none)') AS TEXT)";
            grouped = true;
        }

        // Bucket.
        string bucketExpr = "0";
        bool bucketed = false;
        if (plan is not null)
        {
            _extra.Add(new SearchParameter("$agg_origin", plan.OriginUtc.ToUnixTimeSeconds()));
            _extra.Add(new SearchParameter("$agg_bucket", plan.BucketSeconds));
            bucketExpr = "(CAST(strftime('%s', e.received_utc) AS INTEGER) - $agg_origin) / $agg_bucket";
            bucketed = true;
        }

        // Value.
        if (!TryValueExpr(spec, out string valueExpr, out string? error))
        {
            return CompiledAggregation.Reject(error!);
        }

        string sql = $"""
            SELECT {groupExpr} AS grp, {bucketExpr} AS bkt, {valueExpr} AS val
            FROM events e
            WHERE {predicate.WhereSql}
            GROUP BY grp, bkt;
            """;

        return new CompiledAggregation
        {
            Sql = sql,
            Parameters = [.. predicate.Parameters, .. _extra],
            Grouped = grouped,
            Bucketed = bucketed,
        };
    }

    private bool TryValueExpr(AggregationSpec spec, out string valueExpr, out string? error)
    {
        valueExpr = string.Empty;
        error = null;

        switch (spec.Function)
        {
            case AggregationFunction.Count:
                valueExpr = "COUNT(*)";
                return true;

            case AggregationFunction.DistinctCount:
                {
                    string? field = spec.ValueField ?? spec.GroupByField;
                    if (field is null || !TryScalar(field, "v", out string scalar))
                    {
                        error = "distinct count needs a valid field";
                        return false;
                    }

                    valueExpr = $"COUNT(DISTINCT {scalar})";
                    return true;
                }

            case AggregationFunction.Sum:
            case AggregationFunction.Average:
            case AggregationFunction.Min:
            case AggregationFunction.Max:
                {
                    if (spec.ValueField is null || !EventColumns.TryResolveNumeric(spec.ValueField, out string? column, out string? extractedName))
                    {
                        error = $"'{spec.ValueField}' is not a numeric field";
                        return false;
                    }

                    string operand = column is not null
                        ? $"e.{column}"
                        : $"CAST({ExtractedScalar("v", extractedName!)} AS REAL)";

                    string fn = spec.Function switch
                    {
                        AggregationFunction.Sum => "SUM",
                        AggregationFunction.Average => "AVG",
                        AggregationFunction.Min => "MIN",
                        AggregationFunction.Max => "MAX",
                        _ => throw new InvalidOperationException(),
                    };

                    valueExpr = spec.Function is AggregationFunction.Sum or AggregationFunction.Average
                        ? $"COALESCE({fn}({operand}), 0)"
                        : $"{fn}({operand})";
                    return true;
                }

            default:
                error = "unknown aggregation function";
                return false;
        }
    }

    private bool TryScalar(string field, string prefix, out string scalar)
    {
        scalar = string.Empty;
        if (EventColumns.TryResolveGroup(field, out string? gcol, out string? gname))
        {
            scalar = gcol is not null ? $"e.{gcol}" : ExtractedScalar(prefix, gname!);
            return true;
        }

        if (EventColumns.TryResolveNumeric(field, out string? ncol, out string? nname))
        {
            scalar = ncol is not null ? $"e.{ncol}" : ExtractedScalar(prefix, nname!);
            return true;
        }

        return false;
    }

    private string ExtractedScalar(string prefix, string extractedName)
    {
        string param = string.Create(CultureInfo.InvariantCulture, $"$agg_{prefix}name");
        if (!_extra.Exists(p => p.Name == param))
        {
            _extra.Add(new SearchParameter(param, extractedName));
        }

        return $"(SELECT ef.value FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.name = {param} COLLATE NOCASE LIMIT 1)";
    }
}
