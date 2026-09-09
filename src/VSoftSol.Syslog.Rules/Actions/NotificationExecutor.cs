using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Templating;

namespace VSoftSol.Syslog.Rules.Actions;

/// <summary>Raises a UI notification (PHASE_07 item 3 "RaiseNotification").</summary>
internal sealed class NotificationExecutor : IActionExecutor
{
    public async ValueTask<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var action = (RaiseNotificationAction)context.Action;
        string title = FieldTemplate.Render(action.Title, context.Event).Trim();
        string body = FieldTemplate.Render(action.Body, context.Event);

        if (title.Length == 0)
        {
            title = $"Rule '{context.RuleName}' fired";
        }

        await context.Notifications(action.Level, title, body, context.RuleId, cancellationToken).ConfigureAwait(false);
        return ActionResult.Success($"notification raised ({action.Level})");
    }
}
