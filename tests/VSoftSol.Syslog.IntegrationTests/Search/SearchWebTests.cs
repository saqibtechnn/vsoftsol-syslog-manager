using System.Net;
using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Search;

/// <summary>
/// Phase 5 web surface: route authorization, the search page renders the sidebar + query
/// bar (typing not required), the pattern tester is Operate-only, and the export endpoint
/// authenticates, streams, caps, and writes the audit log.
/// </summary>
public sealed class SearchWebTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public SearchWebTests(SyslogWebApplicationFactory factory) => _factory = factory;

    private async Task SeedEventsAsync(int count, string message = "failed password for root")
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ILogRepository>();
        var events = new List<SyslogEvent>();
        for (int i = 0; i < count; i++)
        {
            events.Add(new SyslogEvent
            {
                ReceivedUtc = DateTimeOffset.UtcNow.AddMinutes(-i),
                SourceIp = "10.0.0.1",
                Hostname = "core-sw-1",
                AppName = "sshd",
                Facility = Facility.SecurityAuth,
                Severity = Severity.Warning,
                Protocol = Protocol.Udp,
                Message = $"{message} {i}",
                RawMessage = Encoding.UTF8.GetBytes(message),
                ParseStatus = ParseStatus.Rfc3164,
            });
        }

        await repo.AppendBatchAsync(events, CancellationToken.None);
        var sqlite = scope.ServiceProvider.GetRequiredService<SqliteLogRepository>();
        await sqlite.SyncSearchIndexAsync(int.MaxValue, CancellationToken.None);
    }

    [Fact]
    public async Task Search_Unauthenticated_RedirectsToLogin()
    {
        var auth = new WebAuthClient(_factory);

        HttpResponseMessage response = await auth.Client.GetAsync("/search");

        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/login");
    }

    [Fact]
    public async Task Search_Authenticated_RendersSidebarAndQueryBar_NoTypingRequired()
    {
        var auth = new WebAuthClient(_factory);
        await auth.SignInAsync("viewer1", "correct horse battery", Role.ReadOnly);

        string html = await auth.Client.GetStringAsync("/search");

        html.Should().Contain("ds-search-sidebar");
        html.Should().Contain("ds-querybar");
        html.Should().Contain("Severity");   // sidebar facet — clickable, no query typed
        html.Should().Contain("Pattern tester");
    }

    [Fact]
    public async Task PatternTester_RequiresOperate_ReadOnlyIsDenied()
    {
        var reader = new WebAuthClient(_factory);
        await reader.SignInAsync("ro-user", "correct horse battery", Role.ReadOnly);
        HttpResponseMessage denied = await reader.Client.GetAsync("/search/pattern-tester");
        denied.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Forbidden);

        var operatorClient = new WebAuthClient(_factory);
        await operatorClient.SignInAsync("op-user", "correct horse battery", Role.Operator);
        HttpResponseMessage allowed = await operatorClient.Client.GetAsync("/search/pattern-tester");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Export_Unauthenticated_IsRefused()
    {
        var auth = new WebAuthClient(_factory);

        HttpResponseMessage response = await auth.Client.GetAsync(
            "/search/export?format=csv&from=2026-01-01T00:00:00Z&to=2027-01-01T00:00:00Z");

        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Export_Csv_StreamsRowsAndWritesTheAuditLog()
    {
        await SeedEventsAsync(20);
        var auth = new WebAuthClient(_factory);
        await auth.SignInAsync("exporter", "correct horse battery", Role.Operator);

        string from = DateTimeOffset.UtcNow.AddHours(-1).ToString("o");
        string to = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
        HttpResponseMessage response = await auth.Client.GetAsync(
            $"/search/export?format=csv&q=failed&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");

        response.StatusCode.Should().Be(HttpStatusCode.OK);
        response.Content.Headers.ContentType!.MediaType.Should().Be("text/csv");
        string csv = await response.Content.ReadAsStringAsync();
        csv.Should().StartWith("event_id,received_utc");
        csv.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length.Should().BeGreaterThan(1);

        using IServiceScope scope = _factory.Services.CreateScope();
        var audit = scope.ServiceProvider.GetRequiredService<SqliteAuditLog>();
        IReadOnlyList<AuditRecord> entries = await audit.QueryAsync(
            new AuditQuery { Action = AuditActions.Export, Limit = 10 }, CancellationToken.None);
        entries.Should().NotBeEmpty();
        entries[0].Actor.Should().Be("exporter");
    }

    [Fact]
    public async Task Export_InvalidQuery_ReturnsBadRequest()
    {
        var auth = new WebAuthClient(_factory);
        await auth.SignInAsync("exporter2", "correct horse battery", Role.Operator);

        string from = DateTimeOffset.UtcNow.AddHours(-1).ToString("o");
        string to = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
        HttpResponseMessage response = await auth.Client.GetAsync(
            $"/search/export?format=csv&q=severity:nonsense&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");

        response.StatusCode.Should().Be(HttpStatusCode.BadRequest);
    }
}
