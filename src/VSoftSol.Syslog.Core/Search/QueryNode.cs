namespace VSoftSol.Syslog.Core.Search;

/// <summary>
/// A node in the parsed search-query tree. The Phase 5 query language parses to this AST;
/// the SQL compiler (Data) walks it to build a parameterised statement, and
/// <see cref="QueryEvaluator"/> walks it to match an in-memory event (the golden oracle).
/// The tree is immutable and closed — every concrete node type is declared in this file.
/// </summary>
public abstract record QueryNode;

/// <summary>An empty query. Matches every event (the time range is still applied upstream).</summary>
public sealed record MatchAllNode : QueryNode
{
    public static MatchAllNode Instance { get; } = new();
}

/// <summary>
/// A free-text term: a bare word or a quoted phrase, matched against the event's
/// full-text content (message body and the lossy raw-text view). <paramref name="Prefix"/>
/// is set when the term ended with a trailing <c>*</c> wildcard — the final token is then
/// prefix-matched.
/// </summary>
public sealed record TextTermNode(string Text, bool Prefix) : QueryNode;

/// <summary>
/// A field comparison: <c>field &lt;op&gt; value</c>. <paramref name="Field"/> is already
/// resolved against the allow-list. <paramref name="Prefix"/> is set for
/// <c>field:value*</c> (only meaningful with <see cref="ComparisonOperator.Equals"/>).
/// </summary>
public sealed record FieldTermNode(
    SearchField Field,
    ComparisonOperator Operator,
    string Value,
    bool Prefix) : QueryNode;

/// <summary>Both operands must match. Produced by <c>AND</c> and by adjacency (implicit AND).</summary>
public sealed record AndNode(QueryNode Left, QueryNode Right) : QueryNode;

/// <summary>Either operand may match. Produced by <c>OR</c>.</summary>
public sealed record OrNode(QueryNode Left, QueryNode Right) : QueryNode;

/// <summary>The operand must not match. Produced by <c>NOT</c> and by a leading <c>-</c>.</summary>
public sealed record NotNode(QueryNode Operand) : QueryNode;
