using System.Data;
using System.Data.Odbc;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.Rules.Templating;

namespace VSoftSol.Syslog.Rules.Actions;

/// <summary>
/// Inserts a row into an external SQL destination over ODBC (PHASE_07 item 3 "WriteToOdbc").
/// The table and column names are identifier-validated (letters / digits / <c>_ .</c> only);
/// every value is a bound <c>?</c> parameter, so a templated field can never break out of
/// the value position. The password, when the connection string needs one, is resolved from
/// the DPAPI secret store by name (<c>PWD={secret}</c> is appended, never persisted).
/// </summary>
internal sealed class OdbcWriteExecutor : IActionExecutor
{
    public async ValueTask<ActionResult> ExecuteAsync(ActionContext context, CancellationToken cancellationToken)
    {
        var action = (WriteToOdbcAction)context.Action;

        if (string.IsNullOrWhiteSpace(action.ConnectionString) || !IsIdentifier(action.TableName)
            || action.ColumnMap.Count == 0 || action.ColumnMap.Keys.Any(k => !IsIdentifier(k)))
        {
            return ActionResult.Permanent("the ODBC action has an invalid connection string, table, or column name");
        }

        string connectionString = action.ConnectionString;
        if (context.Action.SecretName is { } secretName)
        {
            string? password = await context.Secrets(secretName, cancellationToken).ConfigureAwait(false);
            if (password is null)
            {
                return ActionResult.Permanent($"the ODBC secret '{secretName}' is not set");
            }

            connectionString = connectionString.TrimEnd(';') + ";PWD=" + password;
        }

        string[] columns = [.. action.ColumnMap.Keys];
        string columnList = string.Join(", ", columns);
        string placeholders = string.Join(", ", columns.Select(_ => "?"));
        string sql = $"INSERT INTO {action.TableName} ({columnList}) VALUES ({placeholders})";

        int timeout = Math.Clamp(action.TimeoutSeconds, 1, context.Options.MaxActionTimeoutSeconds);

        try
        {
            await using var connection = new OdbcConnection(connectionString);
            using var openCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            openCts.CancelAfter(TimeSpan.FromSeconds(timeout));
            await connection.OpenAsync(openCts.Token).ConfigureAwait(false);

            await using var command = connection.CreateCommand();
            command.CommandText = sql;
            command.CommandTimeout = timeout;
            foreach (string column in columns)
            {
                string value = FieldTemplate.Render(action.ColumnMap[column], context.Event);
                command.Parameters.Add(new OdbcParameter { OdbcType = OdbcType.NVarChar, Value = value });
            }

            int rows = await command.ExecuteNonQueryAsync(cancellationToken).ConfigureAwait(false);
            return ActionResult.Success($"ODBC insert affected {rows} row(s)");
        }
        catch (OdbcException ex)
        {
            // Connection / auth / timeout issues are transient; a bad statement is permanent.
            bool transient = ex.Errors.Cast<OdbcError>().Any(e =>
                e.SQLState.StartsWith("08", StringComparison.Ordinal)   // connection
                || e.SQLState.StartsWith("HYT", StringComparison.Ordinal)); // timeout
            string detail = $"ODBC error {ex.Errors.Cast<OdbcError>().FirstOrDefault()?.SQLState}: {ex.Message}";
            return transient ? ActionResult.Transient(detail) : ActionResult.Permanent(detail);
        }
        catch (Exception ex) when (ex is InvalidOperationException or OperationCanceledException && !cancellationToken.IsCancellationRequested)
        {
            return ActionResult.Transient($"ODBC connection failed: {ex.Message}");
        }
    }

    private static bool IsIdentifier(string? value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 128 || !(char.IsAsciiLetter(value[0]) || value[0] == '_'))
        {
            return false;
        }

        foreach (char c in value)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('_' or '.'))
            {
                return false;
            }
        }

        return true;
    }
}
