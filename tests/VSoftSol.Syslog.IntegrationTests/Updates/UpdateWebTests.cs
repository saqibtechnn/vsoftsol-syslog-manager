using System.Net;
using System.Security.Cryptography;
using FluentAssertions;
using Microsoft.Extensions.DependencyInjection;
using VSoftSol.Syslog.Core.Enums;
using VSoftSol.Syslog.Data.Audit;
using VSoftSol.Syslog.Data.Updates;
using VSoftSol.Syslog.IntegrationTests.TestSupport;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Updates;

/// <summary>
/// Web surface + security for self-update (v1.1 — ADR 0021): the Settings page and the
/// <c>/updates/download</c> endpoint are each independently Administrator-only (per this
/// project's "role re-checked at the service, not just the page" convention — see
/// <c>UpdateEndpoints</c>'s own doc comment), and the download re-verifies the on-disk MSI's
/// hash immediately before streaming it (same defense-in-depth already applied before an
/// archive restore — see <c>ArchiveTamperMatrixTests</c>).
/// </summary>
[Trait("Category", "Updates")]
public sealed class UpdateWebTests : IClassFixture<SyslogWebApplicationFactory>
{
    private readonly SyslogWebApplicationFactory _factory;

    public UpdateWebTests(SyslogWebApplicationFactory factory) => _factory = factory;

    private async Task<(string Path, string Sha256Hex)> SeedReadyMsiAsync(string version = "9.9.9")
    {
        string path = Path.Combine(Path.GetTempPath(), $"update-web-tests-{Guid.NewGuid():N}.msi");
        byte[] bytes = "a genuinely verifiable installer payload"u8.ToArray();
        await File.WriteAllBytesAsync(path, bytes);
        string sha256Hex = Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();

        using IServiceScope scope = _factory.Services.CreateScope();
        var settings = scope.ServiceProvider.GetRequiredService<SqliteUpdateSettingsStore>();
        await settings.RecordUpdateReadyAsync(
            DateTimeOffset.UtcNow, version, manifestJson: "{}", path, sha256Hex, CancellationToken.None);

        return (path, sha256Hex);
    }

    [Fact]
    public async Task UpdatesSettingsPage_Unauthenticated_RedirectsToLogin()
    {
        var auth = new WebAuthClient(_factory);
        HttpResponseMessage response = await auth.Client.GetAsync("/settings/updates");
        response.StatusCode.Should().Be(HttpStatusCode.Redirect);
        response.Headers.Location!.OriginalString.Should().Contain("/login");
    }

    [Theory]
    [InlineData(Role.Operator)]
    [InlineData(Role.ReadOnly)]
    [InlineData(Role.Auditor)]
    public async Task UpdatesSettingsPage_NonAdministratorRoles_AreDenied(Role role)
    {
        var client = new WebAuthClient(_factory);
        await client.SignInAsync($"u-{Guid.NewGuid():N}", "correct horse battery", role);
        HttpResponseMessage denied = await client.Client.GetAsync("/settings/updates");
        denied.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task UpdatesSettingsPage_Administrator_IsAllowed()
    {
        var admin = new WebAuthClient(_factory);
        await admin.SignInAsync($"a-{Guid.NewGuid():N}", "correct horse battery", Role.Administrator);
        HttpResponseMessage allowed = await admin.Client.GetAsync("/settings/updates");
        allowed.StatusCode.Should().Be(HttpStatusCode.OK);
    }

    [Fact]
    public async Task Download_Unauthenticated_IsRefused()
    {
        var auth = new WebAuthClient(_factory);
        HttpResponseMessage response = await auth.Client.GetAsync("/updates/download");
        response.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Unauthorized, HttpStatusCode.Forbidden);
    }

    [Theory]
    [InlineData(Role.Operator)]
    [InlineData(Role.ReadOnly)]
    [InlineData(Role.Auditor)]
    public async Task Download_NonAdministratorRoles_AreDenied(Role role)
    {
        await SeedReadyMsiAsync();

        var client = new WebAuthClient(_factory);
        await client.SignInAsync($"u-{Guid.NewGuid():N}", "correct horse battery", role);
        HttpResponseMessage denied = await client.Client.GetAsync("/updates/download");
        denied.StatusCode.Should().BeOneOf(HttpStatusCode.Redirect, HttpStatusCode.Forbidden);
    }

    [Fact]
    public async Task Download_NoUpdateReady_Returns404()
    {
        var admin = new WebAuthClient(_factory);
        await admin.SignInAsync($"a-{Guid.NewGuid():N}", "correct horse battery", Role.Administrator);

        HttpResponseMessage response = await admin.Client.GetAsync("/updates/download");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }

    [Fact]
    public async Task Download_ReadyAndUntampered_StreamsTheMsiAndWritesTheAuditLog()
    {
        (string path, string _) = await SeedReadyMsiAsync("7.8.9");
        try
        {
            var admin = new WebAuthClient(_factory);
            await admin.SignInAsync($"a-{Guid.NewGuid():N}", "correct horse battery", Role.Administrator);

            HttpResponseMessage response = await admin.Client.GetAsync("/updates/download");
            response.StatusCode.Should().Be(HttpStatusCode.OK);
            response.Content.Headers.ContentType!.MediaType.Should().Be("application/x-msi");
            response.Content.Headers.ContentDisposition!.DispositionType.Should().Be("attachment");
            response.Content.Headers.ContentDisposition!.FileName.Should().Contain("7.8.9");

            byte[] downloaded = await response.Content.ReadAsByteArrayAsync();
            downloaded.Should().Equal(await File.ReadAllBytesAsync(path));

            using IServiceScope scope = _factory.Services.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<SqliteAuditLog>();
            IReadOnlyList<AuditRecord> entries = await audit.QueryAsync(
                new AuditQuery { Action = AuditActions.UpdateMsiDownloadedByAdmin, Limit = 10 }, CancellationToken.None);
            entries.Should().NotBeEmpty();
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Download_TamperedFileOnDisk_IsRefused_AndNoAuditEntryIsWritten()
    {
        (string path, string _) = await SeedReadyMsiAsync("1.2.3");
        try
        {
            byte[] bytes = await File.ReadAllBytesAsync(path);
            bytes[0] ^= 0xFF; // flip a bit — the recorded hash no longer matches
            await File.WriteAllBytesAsync(path, bytes);

            var admin = new WebAuthClient(_factory);
            await admin.SignInAsync($"a-{Guid.NewGuid():N}", "correct horse battery", Role.Administrator);

            HttpResponseMessage response = await admin.Client.GetAsync("/updates/download");
            response.StatusCode.Should().Be(HttpStatusCode.NotFound, "a tampered on-disk file must never be served, even to an admin");

            using IServiceScope scope = _factory.Services.CreateScope();
            var audit = scope.ServiceProvider.GetRequiredService<SqliteAuditLog>();
            IReadOnlyList<AuditRecord> entries = await audit.QueryAsync(
                new AuditQuery { Action = AuditActions.UpdateMsiDownloadedByAdmin, Limit = 50 }, CancellationToken.None);
            entries.Should().NotContain(e => e.Detail != null && e.Detail.Contains("1.2.3"));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task Download_FileDeletedFromDisk_IsRefused()
    {
        (string path, string _) = await SeedReadyMsiAsync("4.5.6");
        File.Delete(path);

        var admin = new WebAuthClient(_factory);
        await admin.SignInAsync($"a-{Guid.NewGuid():N}", "correct horse battery", Role.Administrator);

        HttpResponseMessage response = await admin.Client.GetAsync("/updates/download");
        response.StatusCode.Should().Be(HttpStatusCode.NotFound);
    }
}
