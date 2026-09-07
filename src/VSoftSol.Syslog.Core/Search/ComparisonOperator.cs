namespace VSoftSol.Syslog.Core.Search;

/// <summary>
/// The comparison a <see cref="FieldTermNode"/> applies between a field and a literal.
/// <see cref="Equals"/> is the default when a query writes <c>field:value</c>; the ordering
/// operators come from <c>field:&gt;value</c>, <c>field:&gt;=value</c>, <c>field:&lt;value</c>,
/// <c>field:&lt;=value</c>, and <see cref="NotEquals"/> from <c>field:!=value</c>.
/// </summary>
public enum ComparisonOperator
{
    Equals,
    NotEquals,
    GreaterThan,
    GreaterThanOrEqual,
    LessThan,
    LessThanOrEqual,
}
