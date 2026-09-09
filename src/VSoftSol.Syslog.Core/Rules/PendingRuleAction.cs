namespace VSoftSol.Syslog.Core.Rules;

/// <summary>
/// A side-effecting rule action that matched an event and is to be written to the action
/// outbox in the same transaction as the event (PHASE_07; ADR 0015). Carried transiently
/// on <see cref="Events.SyslogEvent.PendingActions"/> between rule evaluation (ingest path)
/// and commit — never a stored event column.
/// </summary>
/// <param name="RuleId">The rule that fired.</param>
/// <param name="RuleName">The rule's name, for the audit log.</param>
/// <param name="ActionIndex">Position in the rule's (normal or escalation) action list.</param>
/// <param name="Kind">The action kind discriminator, for the outbox <c>kind</c> column.</param>
/// <param name="PayloadJson">The <see cref="RuleAction"/> serialised — never contains a secret value.</param>
/// <param name="WasEscalation">True when this came from the rule's escalation action list.</param>
public sealed record PendingRuleAction(
    long RuleId,
    string RuleName,
    int ActionIndex,
    string Kind,
    string PayloadJson,
    bool WasEscalation);
