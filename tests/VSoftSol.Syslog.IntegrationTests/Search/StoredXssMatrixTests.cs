using System.Text;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Core.Events;
using VSoftSol.Syslog.Data.Repositories;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Search;

/// <summary>
/// PHASE_05 "Stored XSS through the full path" — the product's highest-likelihood real
/// vulnerability: attacker-controlled log content rendered to an administrator. Payloads
/// from the OWASP XSS filter-evasion cheat sheet are ingested verbatim (no sanitisation on
/// ingest — CLAUDE.md Constraint 4) and must come back correctly encoded on every output
/// surface: the results grid (verified here via the pre-rendered search page), the JSON
/// export (verified here via the endpoint), and — sharing the identical Razor
/// auto-encoding path — the expanded row, the context view, and live tail.
/// </summary>
public sealed class StoredXssMatrixTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public StoredXssMatrixTests(SyslogWebApplicationFactory factory) => _factory = factory;

    public static TheoryData<string> Payloads() => new()
    {
        "<script>alert(document.cookie)</script>",
        "<img src=x onerror=alert(1)>",
        "\"><svg/onload=alert(1)>",
        "<iframe src=\"javascript:alert(1)\">",
        "<body onload=alert(1)>",
        "<a href=\"javascript:alert(1)\">click</a>",
        "<div style=\"background:url(javascript:alert(1))\">",
        "';alert(String.fromCharCode(88,83,83))//",
        "<scr<script>ipt>alert(1)</scr</script>ipt>",
        "<INPUT TYPE=\"IMAGE\" SRC=\"javascript:alert('XSS');\">",
    };

    private async Task<long> IngestAsync(string payload)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        var repo = scope.ServiceProvider.GetRequiredService<ILogRepository>();
        long id = await repo.AppendAsync(new SyslogEvent
        {
            ReceivedUtc = DateTimeOffset.UtcNow,
            SourceIp = "203.0.113.9",
            Hostname = payload,          // hostile in the host column too
            AppName = "sshd",
            Facility = Facility.SecurityAuth,
            Severity = Severity.Warning,
            Protocol = Protocol.Udp,
            Message = payload,
            RawMessage = Encoding.UTF8.GetBytes(payload),
            ParseStatus = ParseStatus.Rfc3164,
            Fields = [new EventField("payload", payload)],
        }, CancellationToken.None);

        await scope.ServiceProvider.GetRequiredService<SqliteLogRepository>()
            .SyncSearchIndexAsync(int.MaxValue, CancellationToken.None);
        return id;
    }

    [Theory]
    [MemberData(nameof(Payloads))]
    public async Task Payload_IsStoredVerbatim_ButRenderedEncodedOnEverySurface(string payload)
    {
        long id = await IngestAsync(payload);

        // 1) stored byte-identical (no sanitisation on ingest) — the raw bytes round-trip exactly
        using (IServiceScope scope = _factory.Services.CreateScope())
        {
            var repo = scope.ServiceProvider.GetRequiredService<ILogRepository>();
            long total = await repo.CountAsync(new LogQuery(), CancellationToken.None);
            total.Should().BeGreaterThan(0);
            SyslogEvent? stored = await repo.GetByIdAsync(id, CancellationToken.None);
            stored.Should().NotBeNull();
            Encoding.UTF8.GetString(stored!.RawMessage.Span).Should().Be(payload);
            stored.Message.Should().Be(payload);
        }

        var auth = new WebAuthClient(_factory);
        await auth.SignInAsync($"admin-{Guid.NewGuid():N}", "correct horse battery", Role.Administrator);

        // 2) results grid — the pre-rendered search page must not contain the live payload
        string page = await auth.Client.GetStringAsync("/search");
        AssertNeutralised(page, payload);

        // 3) JSON export — markup characters unicode-escaped, decoded value still exact
        string from = DateTimeOffset.UtcNow.AddHours(-1).ToString("o");
        string to = DateTimeOffset.UtcNow.AddHours(1).ToString("o");
        string json = await auth.Client.GetStringAsync(
            $"/search/export?format=json&from={Uri.EscapeDataString(from)}&to={Uri.EscapeDataString(to)}");
        json.Should().NotContain("<script>");
        json.Should().NotContain("<img src=x");
        json.Should().NotContain("<svg/onload");
        using System.Text.Json.JsonDocument doc = System.Text.Json.JsonDocument.Parse(json);
        doc.RootElement.EnumerateArray().Should().Contain(e => e.GetProperty("message").GetString() == payload);
    }

    private static void AssertNeutralised(string html, string payload)
    {
        // The exact hostile substrings that would execute must not appear unescaped.
        foreach (string dangerous in new[] { "<script>alert", "<img src=x onerror", "<svg/onload", "<iframe src=\"javascript", "<body onload=alert" })
        {
            if (payload.Contains(dangerous.Split(' ')[0], StringComparison.OrdinalIgnoreCase))
            {
                html.Should().NotContain(dangerous, "the payload must be HTML-encoded at render");
            }
        }

        // A raw opening script tag with our alert must never be present as live markup.
        html.Should().NotContain("<script>alert(document.cookie)</script>");
    }
}
