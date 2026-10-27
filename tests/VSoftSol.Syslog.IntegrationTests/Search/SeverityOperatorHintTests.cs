using FluentAssertions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Search;

/// <summary>
/// Severity compares on the syslog code (0 = most severe), so "error or worse" is
/// <c>severity:&lt;=error</c>. Example text in the UI must teach that, not the inverted form that
/// returns informational noise.
/// </summary>
public sealed class SeverityOperatorHintTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public SeverityOperatorHintTests(SyslogWebApplicationFactory factory) => _factory = factory;

    [Theory]
    [InlineData("/search")]
    [InlineData("/dashboards/new/edit")]
    public async Task UiExamples_ShowErrorOrWorseAsLessThanOrEqual(string path)
    {
        var auth = new WebAuthClient(_factory);
        await auth.SignInAsync("sev-hint" + path.Length, "sev-Test-Password-Long-1", Role.Administrator);

        string html = await auth.Client.GetStringAsync(path);

        html.Should().NotContain("severity:&gt;=error", "that example would match error AND everything less severe")
            .And.NotContain("severity:>=error");
    }
}
