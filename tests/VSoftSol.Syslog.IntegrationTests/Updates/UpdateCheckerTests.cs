using System.Text;
using FluentAssertions;
using Microsoft.Extensions.Options;
using VSoftSol.Syslog.Core;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Updates;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Rules.Actions;
using VSoftSol.Syslog.Service.Hosting;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Updates;

/// <summary>
/// <see cref="UpdateChecker"/> orchestration (v1.1 — ADR 0021). Two scenarios are honestly
/// testable in this dev checkout: "disabled" and "no release-signing key configured" — both
/// gate before any network call, and both reflect this repo's *actual* current state
/// (<see cref="ReleaseSigningInfo.HasRealKey"/> is false; no real keypair has been
/// generated). Every code path past that gate (a manifest that verifies against the real
/// key, a tampered one that fails it, the full download-and-stage-a-real-update flow) is —
/// by design, the same design property that makes the trust anchor secure — only
/// exercisable with a real production signing key, which does not and should not exist in
/// this repository or this sandbox. That is not a gap in this test file; it is the intended
/// consequence of "the only acceptable signer is a key the vendor generates and holds
/// themselves" (release-signing/README.md). The plan's own Verification section already
/// calls for a manual end-to-end check with a real signed test release before this feature
/// is considered done — the same disposition as every other "needs real infrastructure this
/// sandbox lacks" item already carried in this project's history.
/// </summary>
[Trait("Category", "Updates")]
public sealed class UpdateCheckerTests
{
    private static UpdateChecker Checker(SqliteTestDatabase db, GitHubUpdateClient client, out List<(string Level, string Title)> notifications)
    {
        var recorded = new List<(string, string)>();
        notifications = recorded;
        var settings = new SqliteUpdateSettingsStore(db.Factory);
        var audit = new SqliteAuditLog(db.Factory);
        NotificationSink sink = (level, title, _, _, _) =>
        {
            recorded.Add((level.ToString(), title));
            return ValueTask.CompletedTask;
        };

        return new UpdateChecker(settings, client, audit, sink, currentVersion: "1.1.0", dataDirectory: Path.GetTempPath());
    }

    [Fact]
    public async Task RunOnceAsync_Disabled_MakesNoNetworkCall_RecordsNothing()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        await using var sink = new HttpSink(async ctx => await ctx.Response.OutputStream.WriteAsync("{}"u8.ToArray()));
        var client = new GitHubUpdateClient(new HttpClient(), Options.Create(new GitHubUpdateOptions
        {
            ApiUrl = $"http://127.0.0.1:{sink.Port}/api",
            RequireHttps = false,
        }));

        UpdateChecker checker = Checker(db, client, out List<(string Level, string Title)> notifications);
        // update_settings is seeded disabled by migration 011 — no explicit save needed.
        await checker.RunOnceAsync(CancellationToken.None);

        sink.RequestCount.Should().Be(0, "a disabled checker must never make an outbound call");
        notifications.Should().BeEmpty();
        (await new SqliteUpdateSettingsStore(db.Factory).GetAsync(CancellationToken.None)).LastCheckedUtc.Should().BeNull();
    }

    [Fact]
    public async Task RunOnceAsync_Enabled_NoReleaseSigningKeyConfigured_RecordsAClearError_MakesNoNetworkCall()
    {
        await using SqliteTestDatabase db = await SqliteTestDatabase.CreateAsync();
        var settingsStore = new SqliteUpdateSettingsStore(db.Factory);
        await settingsStore.SaveEnabledStateAsync(enabled: true, checkIntervalHours: 24, "admin", CancellationToken.None);

        await using var sink = new HttpSink(async ctx => await ctx.Response.OutputStream.WriteAsync("{}"u8.ToArray()));
        var client = new GitHubUpdateClient(new HttpClient(), Options.Create(new GitHubUpdateOptions
        {
            ApiUrl = $"http://127.0.0.1:{sink.Port}/api",
            RequireHttps = false,
        }));

        UpdateChecker checker = Checker(db, client, out List<(string Level, string Title)> notifications);
        await checker.RunOnceAsync(CancellationToken.None);

        ReleaseSigningInfo.HasRealKey.Should().BeFalse("this checkout has no real release-signing key configured");
        sink.RequestCount.Should().Be(0, "there is nothing to verify a manifest against, so checking GitHub at all would be pointless");
        notifications.Should().BeEmpty();

        VSoftSol.Syslog.Data.Updates.UpdateSettings settings = await settingsStore.GetAsync(CancellationToken.None);
        settings.LastCheckError.Should().NotBeNullOrEmpty();
        settings.LastCheckError.Should().ContainEquivalentOf("no release-signing key");
        settings.UpdateReady.Should().BeFalse();
    }
}
