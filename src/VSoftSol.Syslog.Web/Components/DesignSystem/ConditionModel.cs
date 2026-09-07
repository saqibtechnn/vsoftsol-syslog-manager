using System.Text.Json.Serialization;

namespace VSoftSol.Syslog.Web.Components.DesignSystem;

/// <summary>
/// The visual condition tree behind <c>ConditionBuilder</c> (PHASE_04 design system,
/// reused by Phases 6/7/8 for stream match rules, rule conditions, and alert conditions).
/// A node is either a <see cref="ConditionGroup"/> (AND/OR of children) or a
/// <see cref="ConditionComparison"/> (field / operator / value).
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "kind")]
[JsonDerivedType(typeof(ConditionGroup), "group")]
[JsonDerivedType(typeof(ConditionComparison), "comparison")]
public abstract class ConditionNode
{
    public string Id { get; init; } = Guid.NewGuid().ToString("N");
}

public enum ConditionJoin
{
    And,
    Or,
}

public enum ConditionOperator
{
    Equals,
    NotEquals,
    Contains,
    NotContains,
    StartsWith,
    EndsWith,
    Matches,        // regex
    GreaterThan,
    LessThan,
    InList,
    Exists,
    NotExists,
}

public sealed class ConditionGroup : ConditionNode
{
    public ConditionJoin Join { get; set; } = ConditionJoin.And;

    public List<ConditionNode> Children { get; set; } = [];
}

public sealed class ConditionComparison : ConditionNode
{
    public string Field { get; set; } = string.Empty;

    public ConditionOperator Operator { get; set; } = ConditionOperator.Equals;

    public string Value { get; set; } = string.Empty;
}

/// <summary>A field the builder offers, with a friendly label and whether it takes a value.</summary>
public sealed record ConditionField(string Name, string Label);
