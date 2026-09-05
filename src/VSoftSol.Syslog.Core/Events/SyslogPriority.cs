using VSoftSol.Syslog.Core.Enums;

namespace VSoftSol.Syslog.Core.Events;

/// <summary>
/// The syslog PRI value (RFC 5424 §6.2.1): <c>priority = facility * 8 + severity</c>.
/// A value object over the two enums that appear in the canonical schema.
/// </summary>
public readonly record struct SyslogPriority
{
    /// <summary>Highest valid PRI value (Local7 / Debug).</summary>
    public const int MaxValue = (23 * 8) + 7;

    public SyslogPriority(Facility facility, Severity severity)
    {
        Facility = facility;
        Severity = severity;
    }

    public Facility Facility { get; }

    public Severity Severity { get; }

    /// <summary>The encoded PRI value, 0..191.</summary>
    public int Value => ((int)Facility * 8) + (int)Severity;

    /// <summary>Decode a wire PRI value into its facility and severity.</summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is negative or above <see cref="MaxValue"/>.</exception>
    public static SyslogPriority FromValue(int priority)
    {
        if (priority is < 0 or > MaxValue)
        {
            throw new ArgumentOutOfRangeException(nameof(priority), priority, "PRI must be between 0 and 191.");
        }

        return new SyslogPriority((Facility)(priority / 8), (Severity)(priority % 8));
    }

    public override string ToString() => $"<{Value}>";
}
