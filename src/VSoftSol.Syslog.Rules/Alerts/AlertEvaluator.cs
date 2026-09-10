using VSoftSol.Syslog.Core.Alerts;

namespace VSoftSol.Syslog.Rules.Alerts;

/// <summary>One grouping value whose observed metric breached the alert's threshold.</summary>
/// <param name="GroupValue">The group that breached, or null for an ungrouped alert.</param>
/// <param name="ObservedValue">The count / distinct count / silent minutes that breached.</param>
/// <param name="TriggerEventIds">A sample of the events behind the breach (empty for absence / silence).</param>
public sealed record AlertBreach(string? GroupValue, long ObservedValue, IReadOnlyList<long> TriggerEventIds);

/// <summary>The outcome of one evaluation: the groups that are currently breaching.</summary>
public sealed record AlertEvaluation(IReadOnlyList<AlertBreach> Breaches)
{
    public static AlertEvaluation None { get; } = new([]);

    public bool AnyBreach => Breaches.Count > 0;
}

/// <summary>
/// The pure decision half of alert evaluation (PHASE_08). Given a <see cref="CompiledAlert"/>
/// and the already-fetched <see cref="AlertWindowData"/>, it returns the breaching groups —
/// no I/O, no clock except the <paramref name="utcNow"/> passed in, so every boundary and
/// time-travel case is a plain unit test. Deduplication (one open instance per group) and
/// the re-notify interval are the store's / service's job, not this.
/// </summary>
public static class AlertEvaluator
{
    public static AlertEvaluation Evaluate(CompiledAlert alert, AlertWindowData data, DateTimeOffset utcNow)
    {
        ArgumentNullException.ThrowIfNull(alert);
        ArgumentNullException.ThrowIfNull(data);

        return alert.Type switch
        {
            AlertEvaluationType.Threshold => EvaluateThreshold(alert, data),
            AlertEvaluationType.DistinctCount => EvaluateDistinct(alert, data),
            AlertEvaluationType.Absence => EvaluateAbsence(data),
            AlertEvaluationType.DeviceSilent => EvaluateDeviceSilent(data, utcNow),
            _ => AlertEvaluation.None,
        };
    }

    private static AlertEvaluation EvaluateThreshold(CompiledAlert alert, AlertWindowData data)
    {
        var breaches = new List<AlertBreach>();
        foreach (GroupCount gc in data.GroupCounts)
        {
            // "exceeds N" — exactly at the threshold does not fire; N+1 does (PHASE_08 tests).
            if (gc.Count > alert.Threshold)
            {
                breaches.Add(new AlertBreach(gc.GroupValue, gc.Count, gc.SampleEventIds));
            }
        }

        return new AlertEvaluation(breaches);
    }

    private static AlertEvaluation EvaluateDistinct(CompiledAlert alert, AlertWindowData data)
    {
        if (data.DistinctValueCount > alert.Threshold)
        {
            return new AlertEvaluation([new AlertBreach(null, data.DistinctValueCount, data.DistinctSampleEventIds)]);
        }

        return AlertEvaluation.None;
    }

    private static AlertEvaluation EvaluateAbsence(AlertWindowData data)
    {
        // Absence fires when nothing matched. The reader supplies a single ungrouped count;
        // treat "no entry" as zero too.
        long matched = data.GroupCounts.Count == 0 ? 0 : data.GroupCounts[0].Count;
        return matched == 0
            ? new AlertEvaluation([new AlertBreach(null, 0, [])])
            : AlertEvaluation.None;
    }

    private static AlertEvaluation EvaluateDeviceSilent(AlertWindowData data, DateTimeOffset utcNow)
    {
        var breaches = new List<AlertBreach>();
        foreach (DeviceSilence ds in data.DeviceSilences)
        {
            double minutes = ds.LastSeenUtc is { } seen
                ? (utcNow - seen).TotalMinutes
                : double.PositiveInfinity;

            if (minutes > ds.ThresholdMinutes)
            {
                long observed = double.IsInfinity(minutes) ? long.MaxValue : (long)minutes;
                breaches.Add(new AlertBreach(ds.DeviceName, observed, []));
            }
        }

        return new AlertEvaluation(breaches);
    }
}
