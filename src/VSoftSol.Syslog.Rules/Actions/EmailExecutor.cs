using System.Net;
using System.Net.Mail;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Templating;

namespace VSoftSol.Syslog.Rules.Actions;

/// <summary>
/// Sends an email over SMTP with a templated subject/body (PHASE_07 item 3 "SendEmail").
/// CR/LF is stripped from the subject and from every address (SMTP-header-injection
/// defence); recipients come from the rule config, never a template. The password is
/// resolved from the DPAPI secret store by name and is never logged.
/// </summary>
#pragma warning disable SYSLIB0014 // SmtpClient is obsolete; it is the only dependency-free BCL SMTP client and Constraint 2 forbids adding one.
internal sealed class EmailExecutor : IActionExecutor
{
    public async ValueTask<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var action = (SendEmailAction)context.Action;

        if (string.IsNullOrWhiteSpace(action.Host) || action.To.Count == 0 || string.IsNullOrWhiteSpace(action.From))
        {
            return ActionResult.Permanent("the email action is missing a host, sender, or recipient");
        }

        string subject = HeaderSafe(FieldTemplate.Render(action.Subject, context.Event));
        string body = FieldTemplate.Render(action.Body, context.Event);

        using var message = new MailMessage
        {
            From = new MailAddress(HeaderSafe(action.From)),
            Subject = subject.Length == 0 ? $"Rule '{context.RuleName}' fired" : subject,
            Body = body,
            IsBodyHtml = false,
        };

        foreach (string recipient in action.To)
        {
            try
            {
                message.To.Add(new MailAddress(HeaderSafe(recipient)));
            }
            catch (FormatException)
            {
                return ActionResult.Permanent($"'{recipient}' is not a valid email address");
            }
        }

        using var client = new SmtpClient(action.Host, action.Port)
        {
            EnableSsl = action.UseTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 20_000,
        };

        if (!string.IsNullOrWhiteSpace(action.Username) && context.Action.SecretName is { } secretName)
        {
            string? password = await context.Secrets(secretName, cancellationToken).ConfigureAwait(false);
            if (password is null)
            {
                return ActionResult.Permanent($"the SMTP secret '{secretName}' is not set");
            }

            client.Credentials = new NetworkCredential(action.Username, password);
        }

        try
        {
            await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (SmtpException ex)
        {
            int code = (int)ex.StatusCode;
            bool permanent = code is >= 500 and < 600; // 5xx = permanent; 4xx / transport = transient
            string detail = $"SMTP {code}: {ex.Message}";
            return permanent ? ActionResult.Permanent(detail) : ActionResult.Transient(detail);
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return ActionResult.Transient($"SMTP connection failed: {ex.Message}");
        }

        return ActionResult.Success($"emailed {action.To.Count} recipient(s) via {action.Host}");
    }

    private static string HeaderSafe(string value)
    {
        int cut = value.IndexOfAny(['\r', '\n']);
        return (cut >= 0 ? value[..cut] : value).Trim();
    }
}
#pragma warning restore SYSLIB0014
