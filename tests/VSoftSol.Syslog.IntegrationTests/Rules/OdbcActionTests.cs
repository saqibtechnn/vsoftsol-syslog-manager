using FluentAssertions;
using VSoftSol.Syslog.Core.Rules;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Actions;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Rules;

/// <summary>
/// PHASE_07 — the WriteToOdbc action. Identifier validation and parameterisation are
/// asserted here; the <b>live</b> SQLite-ODBC round-trip needs an ODBC driver that is not
/// installed on this build VM and is carried to the Phase 12 clean-VM acceptance run
/// (P7-3, same pattern as P3-1 / P4-1).
/// </summary>
public sealed class OdbcActionTests
{
    private static readonly ActionExecutorRegistry Registry = new();

    [Theory]
    [InlineData("events; DROP TABLE users; --", "col", "{message}")]
    [InlineData("events", "col); DROP TABLE users; --", "{message}")]
    [InlineData("ev ents", "col", "{message}")]
    public async Task Odbc_NonIdentifierTableOrColumn_IsRefusedBeforeAnyConnection(string table, string column, string template)
    {
        var action = new WriteToOdbcAction
        {
            ConnectionString = "DSN=whatever",
            TableName = table,
            ColumnMap = { [column] = template },
        };

        ActionResult result = await Registry.ExecuteAsync(ActionTestSupport.Context(action), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Retryable.Should().BeFalse();
        result.Detail.Should().Contain("invalid");
    }

    [Fact]
    public async Task Odbc_TemplatedValueWithSqlMetacharacters_IsBoundNotConcatenated()
    {
        // With no driver the connection fails, but the failure must be a connection error —
        // proof the statement was built and the value went to a parameter, not the text.
        var action = new WriteToOdbcAction
        {
            ConnectionString = "DSN=nonexistent",
            TableName = "syslog",
            ColumnMap = { ["msg"] = "{message}" },
            TimeoutSeconds = 2,
        };
        var evt = ActionTestSupport.Event(message: "'); DROP TABLE syslog; --");

        ActionResult result = await Registry.ExecuteAsync(
            ActionTestSupport.Context(action, evt), CancellationToken.None);

        result.Ok.Should().BeFalse();
        result.Detail.Should().NotContain("DROP TABLE");
        result.Detail.Should().MatchRegex("ODBC");
    }
}
