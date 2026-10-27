using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Updates;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using VSoftSol.Syslog.Service.Hosting;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Updates;

/// <summary>
/// The Settings → Updates status line must tell the truth. Two lies were observed in a rehearsal:
/// it kept saying "Update ready" with a working download link after the staged installer had been
/// discarded (the download correctly returned 404), and it hid a later failed check behind the
/// earlier "ready" message. A missing-manifest error also leaked an internal option name.
/// </summary>
[Trait("Category", "Updates")]
public sealed class UpdatePageStatusTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public UpdatePageStatusTests(SyslogWebApplicationFactory factory) => _factory = factory;

    private async Task<string> SeedReadyAsync()
    {
        string path = Path.Combine(Path.GetTempPath(), $"update-status-{Guid.NewGuid():N}.msi");
        byte[] bytes = "a genuinely verifiable installer payload"u8.ToArray();
        await File.WriteAllBytesAsync(path, bytes);

        using IServiceScope scope = _factory.Services.CreateScope();
        var store = scope.ServiceProvider.GetRequiredService<SqliteUpdateSettingsStore>();
        await store.RecordUpdateReadyAsync(
            DateTimeOffset.UtcNow, "9.9.9", "{}", path, Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant(), CancellationToken.None);
        return path;
    }

    private async Task RecordFailedCheckAsync(string error)
    {
        using IServiceScope scope = _factory.Services.CreateScope();
        await scope.ServiceProvider.GetRequiredService<SqliteUpdateSettingsStore>()
            .RecordCheckResultAsync(DateTimeOffset.UtcNow, error, CancellationToken.None);
    }

    private async Task<string> PageAsync()
    {
        var admin = new WebAuthClient(_factory);
        await admin.SignInAsync($"a-{Guid.NewGuid():N}", "correct horse battery", Role.Administrator);
        return await admin.Client.GetStringAsync("/settings/updates");
    }

    [Fact]
    public async Task ReadyInstaller_AndALaterFailedCheck_ShowBothMessages()
    {
        string path = await SeedReadyAsync();
        await RecordFailedCheckAsync("could not reach the update server or parse its response");
        try
        {
            string html = await PageAsync();

            html.Should().Contain("Update ready", "the verified installer is still staged and downloadable")
                .And.Contain("Last check failed", "a later failure must not be hidden")
                .And.Contain("could not reach the update server");
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task ReadyInstaller_WhoseFileIsGone_IsNotReportedAsReady()
    {
        string path = await SeedReadyAsync();
        File.Delete(path);

        string html = await PageAsync();

        html.Should().NotContain("Update ready")
            .And.NotContain("Download the installer")
            .And.Contain("no longer on disk", "the admin is told what happened and what to do");
    }

    [Fact]
    public void NoManifestMessage_IsPlainEnglish_AndNeverNamesAnInternalOption()
    {
        string message = UpdateChecker.NoManifestMessage("1.1.4");

        message.Should().Contain("1.1.4").And.Contain("update-manifest.json").And.Contain("signed");
        message.Should().NotContain("ManifestAssetName");
    }
}
