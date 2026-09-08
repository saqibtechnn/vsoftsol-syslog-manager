using System.Text.Json.Serialization;

namespace VSoftSol.Syslog.Core.Conditions;

/// <summary>
/// The visual condition tree (PHASE_04 design system's <c>ConditionBuilder</c>, reused by
/// Phase 6 stream match rules, Phase 7 rule conditions, and Phase 8 alert conditions). A
/// node is either a <see cref="ConditionGroup"/> (AND/OR of children) or a
/// <see cref="ConditionComparison"/> (field / operator / value). Lives in Core so the
/// ingest-path evaluator (<c>VSoftSol.Syslog.Rules</c>) and the UI share one model
/// (ADR 0014).
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

/// <summary>A field the builder offers, with a friendly label.</summary>
public sealed record ConditionField(string Name, string Label);
