using FsCheck;
using FsCheck.Xunit;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;

namespace VSoftSol.Syslog.UnitTests.Domain;

/// <summary>
/// Property-based coverage (TESTING_STANDARDS.md §1). FsCheck is wired in Phase 0 so the
/// parser and rules phases have no excuse to skip it.
/// </summary>
public sealed class SyslogPriorityPropertyTests
{
    private static readonly Arbitrary<(Facility Facility, Severity Severity)> PriorityParts =
        Arb.From(
            from facility in Gen.Choose(0, 23)
            from severity in Gen.Choose(0, 7)
            select ((Facility)facility, (Severity)severity));

    [Property]
    public Property Encode_ThenDecode_RoundTrips() =>
        Prop.ForAll(PriorityParts, parts =>
        {
            var original = new SyslogPriority(parts.Facility, parts.Severity);
            return SyslogPriority.FromValue(original.Value) == original;
        });

    [Property]
    public Property Value_IsAlways_WithinRfcRange() =>
        Prop.ForAll(PriorityParts, parts =>
        {
            int value = new SyslogPriority(parts.Facility, parts.Severity).Value;
            return value is >= 0 and <= SyslogPriority.MaxValue;
        });
}
