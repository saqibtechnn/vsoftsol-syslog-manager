using System.Globalization;
using VSoftSol.Syslog.Core.Search;
using VSoftSol.Syslog.Core.Security;
using VSoftSol.Syslog.Data.Repositories;

namespace VSoftSol.Syslog.Data.Search;

/// <summary>
/// Compiles a parsed <see cref="QueryNode"/> tree, the sidebar's structured filters, and
/// the principal's <see cref="UserScope"/> into a single parameterised SQL <c>WHERE</c>
/// body plus an <c>ORDER BY</c>. Every user-supplied value becomes a bound parameter —
/// nothing user-supplied is concatenated into SQL text (SECURITY_STANDARDS.md §5.1). The
/// generated predicates mirror <see cref="QueryEvaluator"/> exactly, including SQLite's
/// three-valued handling of NULL columns and <c>NOT EXISTS</c>, so the SQL path and the
/// golden oracle return identical result sets.
/// </summary>
internal sealed class SearchCompiler
{
    private readonly List<SearchParameter> _parameters = [];
    private int _next;

    public CompiledSearch Compile(SearchRequest request, QueryNode ast, UserScope scope)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(ast);
        ArgumentNullException.ThrowIfNull(scope);

        var clauses = new List<string>
        {
            $"e.received_utc >= {Bind(StorageFormat.Timestamp(request.FromUtc))}",
            $"e.received_utc < {Bind(StorageFormat.Timestamp(request.ToUtc))}",
        };

        if (request.Severities.Count > 0)
        {
            clauses.Add($"e.severity IN ({BindList(request.Severities.Select(s => (object)(int)s))})");
        }

        if (request.DeviceIds.Count > 0)
        {
            clauses.Add($"e.device_id IN ({BindList(request.DeviceIds.Select(d => (object)d))})");
        }

        if (request.StreamIds.Count > 0)
        {
            clauses.Add(
                "EXISTS (SELECT 1 FROM event_streams es WHERE es.event_id = e.event_id " +
                $"AND es.stream_id IN ({BindList(request.StreamIds.Select(s => (object)s))}))");
        }

        bool impossible = AppendScopeClauses(scope, clauses);

        string? queryClause = CompileNode(ast);
        if (queryClause is not null)
        {
            clauses.Add(queryClause);
        }

        return new CompiledSearch
        {
            WhereSql = string.Join(" AND ", clauses),
            OrderBySql = OrderBy(request.Sort),
            Parameters = _parameters,
            MatchesNothing = impossible,
        };
    }

    // --------------------------------------------------------------- scope

    private bool AppendScopeClauses(UserScope scope, List<string> clauses)
    {
        if (scope.IsUnrestricted)
        {
            return false;
        }

        if (!scope.AllStreams)
        {
            if (scope.StreamIds.Count == 0)
            {
                return true; // fail closed
            }

            clauses.Add(
                "EXISTS (SELECT 1 FROM event_streams es WHERE es.event_id = e.event_id " +
                $"AND es.stream_id IN ({BindList(scope.StreamIds.Select(s => (object)s))}))");
        }

        if (!scope.AllDeviceGroups)
        {
            if (scope.DeviceGroupIds.Count == 0)
            {
                return true;
            }

            clauses.Add(
                "e.device_id IN (SELECT device_id FROM device_group_members " +
                $"WHERE group_id IN ({BindList(scope.DeviceGroupIds.Select(g => (object)g))}))");
        }

        return false;
    }

    // --------------------------------------------------------------- AST

    private string? CompileNode(QueryNode node)
    {
        // Collapse a maximal pure-text subtree into ONE FTS5 boolean MATCH — FTS5 evaluates
        // `"a" AND ("b" OR "c") NOT "d"` in a single posting-list pass. Emitting a subquery
        // per term (and especially a correlated one) turns a boolean text query into an
        // N× full-text scan; on 2M events that is the difference between ~1 s and ~12 s.
        if (node is not (MatchAllNode or FieldTermNode) && IsPureText(node) && TryFtsExpr(node) is { } ftsExpr)
        {
            return $"e.event_id IN (SELECT rowid FROM events_fts WHERE events_fts MATCH {Bind(ftsExpr)})";
        }

        return node switch
        {
            MatchAllNode => null,
            AndNode a => CombineAnd(CompileNode(a.Left), CompileNode(a.Right)),
            OrNode o => CombineOr(CompileNode(o.Left), CompileNode(o.Right)),
            // IFNULL(..., 0): SQLite's NOT of a NULL is NULL (row excluded), but the oracle's
            // boolean !A treats a non-matching (incl. NULL-column) operand as false, so NOT A
            // is true. Coercing the operand to 0 first keeps the two paths identical — and it
            // is the better UX (NOT host:web01 does include events with no host recorded).
            NotNode n => $"NOT (IFNULL({CompileNode(n.Operand) ?? "1"}, 0))",
            TextTermNode t => TextClause(t.Text, t.Prefix),
            FieldTermNode f => FieldClause(f),
            _ => throw new InvalidOperationException($"Unhandled query node {node.GetType().Name}."),
        };
    }

    /// <summary>The subtree is free-text only, and every term tokenises to at least one token.</summary>
    private static bool IsPureText(QueryNode node) => node switch
    {
        TextTermNode t => FtsTokenizer.Tokenize(t.Text).Count > 0,
        AndNode a => IsPureText(a.Left) && IsPureText(a.Right),
        OrNode o => IsPureText(o.Left) && IsPureText(o.Right),
        NotNode n => IsPureText(n.Operand),
        _ => false,
    };

    /// <summary>
    /// An FTS5 boolean expression for a pure-text subtree, or null when the structure
    /// cannot be written as one FTS5 query (a bare unary <c>NOT</c>, or a <c>NOT</c> nested
    /// under an <c>OR</c> — FTS5's <c>NOT</c> is the binary "x but not y" form).
    /// </summary>
    private static string? TryFtsExpr(QueryNode node) => node switch
    {
        TextTermNode t => FtsPhrase(t),
        AndNode a => FtsAnd(a.Left, a.Right),
        OrNode o => !ContainsNot(o) && TryFtsExpr(o.Left) is { } l && TryFtsExpr(o.Right) is { } r
            ? $"({l} OR {r})"
            : null,
        NotNode => null,
        _ => null,
    };

    private static string? FtsAnd(QueryNode left, QueryNode right)
    {
        // X AND NOT Y  ->  FTS5  X NOT Y   (Y must itself be positive)
        if (right is NotNode rn && !ContainsNot(rn.Operand) && TryFtsExpr(left) is { } lx && TryFtsExpr(rn.Operand) is { } ry)
        {
            return $"({lx} NOT {ry})";
        }

        if (left is NotNode ln && !ContainsNot(ln.Operand) && TryFtsExpr(right) is { } rx && TryFtsExpr(ln.Operand) is { } ly)
        {
            return $"({rx} NOT {ly})";
        }

        return TryFtsExpr(left) is { } a && TryFtsExpr(right) is { } b ? $"({a} AND {b})" : null;
    }

    private static bool ContainsNot(QueryNode node) => node switch
    {
        NotNode => true,
        AndNode a => ContainsNot(a.Left) || ContainsNot(a.Right),
        OrNode o => ContainsNot(o.Left) || ContainsNot(o.Right),
        _ => false,
    };

    private static string FtsPhrase(TextTermNode t)
    {
        IReadOnlyList<string> tokens = FtsTokenizer.Tokenize(t.Text);
        return "\"" + string.Join(' ', tokens) + "\"" + (t.Prefix ? " *" : string.Empty);
    }

    private static string? CombineAnd(string? left, string? right) =>
        (left, right) switch
        {
            (null, null) => null,
            (null, _) => right,
            (_, null) => left,
            _ => $"({left} AND {right})",
        };

    private static string? CombineOr(string? left, string? right) =>
        left is null || right is null ? null : $"({left} OR {right})";

    private string TextClause(string text, bool prefix)
    {
        IReadOnlyList<string> tokens = FtsTokenizer.Tokenize(text);
        if (tokens.Count == 0)
        {
            return "0"; // an FTS match with no tokens matches nothing
        }

        string phrase = "\"" + string.Join(' ', tokens) + "\"" + (prefix ? " *" : string.Empty);

        // Single FTS evaluation into an ephemeral index, then the outer query filters/sorts.
        // Used only for the residual cases the combined-FTS pass above cannot fold (a text
        // term structurally mixed with a field term, or `message:`/`raw:` field terms).
        return $"e.event_id IN (SELECT rowid FROM events_fts WHERE events_fts MATCH {Bind(phrase)})";
    }

    private string FieldClause(FieldTermNode f) => f.Field.ValueType switch
    {
        SearchValueType.FreeText => FreeTextField(f),
        SearchValueType.Text or SearchValueType.IpAddress => StringColumnClause(ColumnFor(f.Field.CanonicalName), f),
        SearchValueType.Severity => NumericColumnClause("e.severity", f),
        SearchValueType.Facility => NumericColumnClause("e.facility", f),
        SearchValueType.Number => NumericColumnClause(ColumnFor(f.Field.CanonicalName), f),
        SearchValueType.Timestamp => TimestampClause(f),
        SearchValueType.Protocol => TokenColumnClause("e.protocol", f),
        SearchValueType.ParseStatus => TokenColumnClause("e.parse_status", f),
        SearchValueType.Reference => f.Field.CanonicalName == "device" ? DeviceClause(f) : StreamClause(f),
        SearchValueType.CustomField => CustomFieldClause(f),
        _ => throw new InvalidOperationException($"Unhandled field type {f.Field.ValueType}."),
    };

    private string FreeTextField(FieldTermNode f)
    {
        string inner = TextClause(f.Value, f.Prefix);
        return f.Operator == ComparisonOperator.NotEquals ? $"NOT ({inner})" : inner;
    }

    private static string ColumnFor(string canonical) => canonical switch
    {
        "host" => "e.hostname",
        "source_ip" => "e.source_ip",
        "app" => "e.app_name",
        "proc_id" => "e.proc_id",
        "msg_id" => "e.msg_id",
        "vendor" => "e.vendor",
        "event_id" => "e.event_id",
        "device_id" => "e.device_id",
        _ => throw new InvalidOperationException($"No column mapping for '{canonical}'."),
    };

    private string StringColumnClause(string column, FieldTermNode f)
    {
        if (f.Operator == ComparisonOperator.NotEquals)
        {
            return $"({column} IS NOT NULL AND {column} <> {Bind(f.Value)} COLLATE NOCASE)";
        }

        return f.Prefix
            ? $"{column} LIKE {Bind(EscapeLike(f.Value) + "%")} ESCAPE '\\'"
            : $"{column} = {Bind(f.Value)} COLLATE NOCASE";
    }

    private string TokenColumnClause(string column, FieldTermNode f)
    {
        if (f.Operator == ComparisonOperator.NotEquals)
        {
            return $"{column} <> {Bind(f.Value)}";
        }

        return f.Prefix
            ? $"{column} LIKE {Bind(EscapeLike(f.Value) + "%")} ESCAPE '\\'"
            : $"{column} = {Bind(f.Value)}";
    }

    private string NumericColumnClause(string column, FieldTermNode f)
    {
        long value = long.Parse(f.Value, CultureInfo.InvariantCulture);
        return $"{column} {SqlOperator(f.Operator)} {Bind(value)}";
    }

    private string TimestampClause(FieldTermNode f)
    {
        string column = f.Field.CanonicalName == "event_time" ? "e.event_utc" : "e.received_utc";
        var parsed = DateTimeOffset.Parse(f.Value, CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal);
        return $"{column} {SqlOperator(f.Operator)} {Bind(StorageFormat.Timestamp(parsed))}";
    }

    private string DeviceClause(FieldTermNode f)
    {
        string match = f.Prefix
            ? $"name LIKE {Bind(EscapeLike(f.Value) + "%")} ESCAPE '\\'"
            : $"name = {Bind(f.Value)} COLLATE NOCASE";
        string subquery = $"SELECT device_id FROM devices WHERE {match}";

        return f.Operator == ComparisonOperator.NotEquals
            ? $"(e.device_id IS NOT NULL AND e.device_id NOT IN ({subquery}))"
            : $"e.device_id IN ({subquery})";
    }

    private string StreamClause(FieldTermNode f)
    {
        string match = f.Prefix
            ? $"s.name LIKE {Bind(EscapeLike(f.Value) + "%")} ESCAPE '\\'"
            : $"s.name = {Bind(f.Value)} COLLATE NOCASE";
        string exists =
            "SELECT 1 FROM event_streams es JOIN streams s ON s.stream_id = es.stream_id " +
            $"WHERE es.event_id = e.event_id AND {match}";

        return f.Operator == ComparisonOperator.NotEquals
            ? $"NOT EXISTS ({exists})"
            : $"EXISTS ({exists})";
    }

    private string CustomFieldClause(FieldTermNode f)
    {
        string nameParam = Bind(f.Field.CustomFieldName!);
        string head = $"SELECT 1 FROM event_fields ef WHERE ef.event_id = e.event_id AND ef.name = {nameParam} COLLATE NOCASE";

        string valueMatch;
        switch (f.Operator)
        {
            case ComparisonOperator.Equals when f.Prefix:
                valueMatch = $"ef.value LIKE {Bind(EscapeLike(f.Value) + "%")} ESCAPE '\\'";
                break;
            case ComparisonOperator.Equals:
            case ComparisonOperator.NotEquals:
                valueMatch = $"ef.value = {Bind(f.Value)} COLLATE NOCASE";
                break;
            default:
                double numeric = double.Parse(f.Value, NumberStyles.Float, CultureInfo.InvariantCulture);
                valueMatch = $"CAST(ef.value AS REAL) {SqlOperator(f.Operator)} {Bind(numeric)}";
                break;
        }

        return f.Operator == ComparisonOperator.NotEquals
            ? $"NOT EXISTS ({head} AND {valueMatch})"
            : $"EXISTS ({head} AND {valueMatch})";
    }

    private static string SqlOperator(ComparisonOperator op) => op switch
    {
        ComparisonOperator.Equals => "=",
        ComparisonOperator.NotEquals => "<>",
        ComparisonOperator.GreaterThan => ">",
        ComparisonOperator.GreaterThanOrEqual => ">=",
        ComparisonOperator.LessThan => "<",
        ComparisonOperator.LessThanOrEqual => "<=",
        _ => throw new InvalidOperationException($"Unhandled operator {op}."),
    };

    private static string OrderBy(SearchSort sort)
    {
        string column = sort.Field switch
        {
            SearchSortField.ReceivedUtc => "e.received_utc",
            SearchSortField.EventUtc => "e.event_utc",
            SearchSortField.Severity => "e.severity",
            SearchSortField.Facility => "e.facility",
            SearchSortField.Host => "e.hostname",
            SearchSortField.SourceIp => "e.source_ip",
            SearchSortField.App => "e.app_name",
            SearchSortField.Vendor => "e.vendor",
            _ => "e.received_utc",
        };

        string direction = sort.Descending ? "DESC" : "ASC";
        return $"{column} {direction}, e.event_id {direction}";
    }

    private static string EscapeLike(string value) => value
        .Replace("\\", "\\\\", StringComparison.Ordinal)
        .Replace("%", "\\%", StringComparison.Ordinal)
        .Replace("_", "\\_", StringComparison.Ordinal);

    private string Bind(object value)
    {
        string name = string.Create(CultureInfo.InvariantCulture, $"$p{_next++}");
        _parameters.Add(new SearchParameter(name, value));
        return name;
    }

    private string BindList(IEnumerable<object> values)
    {
        var names = new List<string>();
        foreach (object value in values)
        {
            names.Add(Bind(value));
        }

        return names.Count == 0 ? "NULL" : string.Join(", ", names);
    }
}
