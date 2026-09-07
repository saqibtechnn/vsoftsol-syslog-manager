using FluentAssertions;
using VSoftSol.Syslog.Data.Audit;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Security;

/// <summary>
/// PHASE_04: the before/after JSON diff on a config change, with secret redaction
/// (SECURITY_STANDARDS.md §5.5 — secrets never in audit diffs).
/// </summary>
public sealed class AuditDiffTests
{
    private sealed record SmtpSettings(string Host, int Port, string Username, string Password, string ApiToken);

    [Fact]
    public void Snapshot_RedactsPropertiesThatLookLikeSecrets()
    {
        var settings = new SmtpSettings("smtp.example.com", 587, "mailer", "hunter2-plaintext", "tok_live_abc123");

        string? json = AuditDiff.Snapshot(settings);

        json.Should().NotBeNull();
        json.Should().Contain("smtp.example.com").And.Contain("587").And.Contain("mailer");
        json.Should().NotContain("hunter2-plaintext");
        json.Should().NotContain("tok_live_abc123");
        json.Should().Contain(AuditDiff.RedactedMarker);
    }

    [Fact]
    public void Snapshot_RedactsExplicitlyNamedKeys()
    {
        var config = new { WebhookUrl = "https://hooks.example.com/x", SharedValue = "keep-me" };

        string? json = AuditDiff.Snapshot(config, extraRedactKeys: ["WebhookUrl"]);

        json.Should().NotContain("hooks.example.com");
        json.Should().Contain("keep-me");
    }

    [Fact]
    public void Snapshot_OfNull_ReturnsNull()
    {
        AuditDiff.Snapshot(null).Should().BeNull();
    }

    [Fact]
    public void ChangedKeys_ReturnsOnlyThePropertiesThatDiffer()
    {
        string? before = AuditDiff.Snapshot(new { Port = 514, Enabled = true, Name = "primary" });
        string? after = AuditDiff.Snapshot(new { Port = 1514, Enabled = true, Name = "primary" });

        AuditDiff.ChangedKeys(before, after).Should().Equal("Port");
    }

    [Fact]
    public void ChangedKeys_WhenNothingChanged_IsEmpty()
    {
        string? snap = AuditDiff.Snapshot(new { A = 1, B = "two" });

        AuditDiff.ChangedKeys(snap, snap).Should().BeEmpty();
    }
}
