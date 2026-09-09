using VSoftSol.Syslog.Core.Rules;

namespace VSoftSol.Syslog.Rules.Actions;

/// <summary>
/// Maps a side-effecting <see cref="RuleAction"/> to the executor for its kind and runs it
/// once (PHASE_07). This is a closed set of strategies — not a seam — so the registry is a
/// plain switch, not a DI collection. The dispatcher (composition root) owns retry / audit /
/// outbox state around a single call to <see cref="ExecuteAsync"/>.
/// </summary>
public sealed class ActionExecutorRegistry
{
    private readonly EmailExecutor _email = new();
    private readonly WebhookExecutor _webhook = new();
    private readonly ScriptExecutor _script = new();
    private readonly SyslogForwardExecutor _forward = new();
    private readonly FileWriteExecutor _file = new();
    private readonly OdbcWriteExecutor _odbc = new();
    private readonly NotificationExecutor _notify = new();

    public async ValueTask<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(context);

        IActionExecutor executor = context.Action switch
        {
            SendEmailAction => _email,
            HttpWebhookAction => _webhook,
            RunScriptAction => _script,
            ForwardSyslogAction => _forward,
            WriteToFileAction => _file,
            WriteToOdbcAction => _odbc,
            RaiseNotificationAction => _notify,
            _ => throw new InvalidOperationException(
                $"'{RuleActionInfo.Kind(context.Action)}' is not a dispatchable side-effecting action"),
        };

        try
        {
            return await executor.ExecuteAsync(context, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // An executor must never throw past here — that would stall the dispatcher.
            return ActionResult.Transient($"{RuleActionInfo.Label(context.Action)} threw: {ex.GetType().Name}: {ex.Message}");
        }
    }
}
