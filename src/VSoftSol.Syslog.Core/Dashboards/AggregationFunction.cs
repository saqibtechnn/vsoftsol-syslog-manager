namespace VSoftSol.Syslog.Core.Dashboards;

/// <summary>
/// How a widget reduces the events in its window to a number (PHASE_09 build item 2).
/// <see cref="Count"/> and <see cref="DistinctCount"/> need no value field; the rest
/// require a numeric one.
/// </summary>
public enum AggregationFunction
{
    /// <summary>Number of matching events.</summary>
    Count,

    /// <summary>Number of distinct values of the group-by / value field.</summary>
    DistinctCount,

    /// <summary>Sum of the value field.</summary>
    Sum,

    /// <summary>Mean of the value field.</summary>
    Average,

    /// <summary>Smallest value of the value field.</summary>
    Min,

    /// <summary>Largest value of the value field.</summary>
    Max,
}
