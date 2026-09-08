using VSoftSol.Syslog.Core.Conditions;

namespace VSoftSol.Syslog.Web.Streams;

/// <summary>The field list the visual condition builder offers for stream / rule conditions.</summary>
public static class ConditionFieldCatalog
{
    public static IReadOnlyList<ConditionField> Fields { get; } =
        ConditionFields.BuiltIn.Select(f => new ConditionField(f.Name, f.Label)).ToList();
}
