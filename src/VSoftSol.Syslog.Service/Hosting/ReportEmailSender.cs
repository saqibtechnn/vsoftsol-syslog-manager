using System.Net;
using System.Net.Mail;
using VSoftSol.Syslog.Data.Reports;
using VSoftSol.Syslog.Data.Secrets;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// Sends a generated report by email using the single global SMTP profile
/// (PHASE_10 build item 8). Mirrors <c>Rules.Actions.EmailExecutor</c>'s header-injection
/// defence and secret handling, simplified for one fixed profile rather than a per-rule one.
/// </summary>
#pragma warning disable SYSLIB0014 // SmtpClient is obsolete; the only dependency-free BCL SMTP client (CLAUDE.md Constraint 2).
public sealed class ReportEmailSender
{
    private readonly SqliteReportSmtpSettingsStore _settings;
    private readonly SqliteSecretStore _secrets;

    public ReportEmailSender(SqliteReportSmtpSettingsStore settings, SqliteSecretStore secrets)
    {
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _secrets = secrets ?? throw new ArgumentNullException(nameof(secrets));
    }

    public async Task<(bool Ok, string? Error)> SendAsync(
        IReadOnlyList<string> recipients, string subject, string body,
        IReadOnlyList<(string FileName, byte[] Content, string ContentType)> attachments,
        CancellationToken cancellationToken)
    {
        ReportSmtpSettings settings = await _settings.GetAsync(cancellationToken).ConfigureAwait(false);
        if (!settings.IsConfigured)
        {
            return (false, "SMTP delivery is not configured (Settings > Report delivery).");
        }

        using var message = new MailMessage
        {
            From = new MailAddress(HeaderSafe(settings.FromAddress!)),
            Subject = HeaderSafe(subject),
            Body = body,
            IsBodyHtml = false,
        };

        foreach (string recipient in recipients)
        {
            try
            {
                message.To.Add(new MailAddress(HeaderSafe(recipient)));
            }
            catch (FormatException)
            {
                return (false, $"'{recipient}' is not a valid email address");
            }
        }

        foreach ((string fileName, byte[] content, string contentType) in attachments)
        {
            message.Attachments.Add(new Attachment(new MemoryStream(content), fileName, contentType));
        }

        using var client = new SmtpClient(settings.Host, settings.Port)
        {
            EnableSsl = settings.UseTls,
            DeliveryMethod = SmtpDeliveryMethod.Network,
            Timeout = 30_000,
        };

        if (!string.IsNullOrWhiteSpace(settings.Username) && settings.SecretName is { } secretName)
        {
            string? password = await _secrets.GetAsync(secretName, cancellationToken).ConfigureAwait(false);
            if (password is null)
            {
                return (false, $"the SMTP secret '{secretName}' is not set");
            }

            client.Credentials = new NetworkCredential(settings.Username, password);
        }

        try
        {
            await client.SendMailAsync(message, cancellationToken).ConfigureAwait(false);
        }
        catch (SmtpException ex)
        {
            return (false, $"SMTP {(int)ex.StatusCode}: {ex.Message}");
        }
        catch (Exception ex) when (ex is IOException or InvalidOperationException or System.Net.Sockets.SocketException)
        {
            return (false, $"SMTP connection failed: {ex.Message}");
        }

        return (true, null);
    }

    private static string HeaderSafe(string value)
    {
        int cut = value.IndexOfAny(['\r', '\n']);
        return (cut >= 0 ? value[..cut] : value).Trim();
    }
}
#pragma warning restore SYSLIB0014
