namespace VSoftSol.Syslog.Reporting.Export;

/// <summary>
/// Neutralises spreadsheet formula injection in exported CSV cells (OWASP "CSV Injection").
/// A cell whose first character is one a spreadsheet treats as the start of a formula
/// (<c>= + - @</c>) or a control character (TAB, CR) is prefixed with a single quote so
/// Excel / LibreOffice / Sheets render it as text. Applied <b>only on export</b> — the
/// stored value is never altered (CLAUDE.md Constraint 4; PHASE_05: "byte-identical in the
/// database").
/// </summary>
public static class CsvFormulaGuard
{
    public static bool NeedsGuard(string? value) =>
        !string.IsNullOrEmpty(value) && value[0] is '=' or '+' or '-' or '@' or '\t' or '\r';

    public static string Guard(string? value) =>
        NeedsGuard(value) ? "'" + value : value ?? string.Empty;
}
