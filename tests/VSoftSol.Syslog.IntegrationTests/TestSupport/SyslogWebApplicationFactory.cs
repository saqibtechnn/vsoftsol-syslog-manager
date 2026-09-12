using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using VSoftSol.Syslog.Data.Security;
using VSoftSol.Syslog.Data.Seed;
using VSoftSol.Syslog.Data.Users;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>
/// Hosts the Web app with its data directory and database redirected to a throwaway temp
/// location, so the real ProgramData store is never touched by tests.
/// </summary>
public sealed class SyslogWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "vsoftsol-web-" + Guid.NewGuid().ToString("N"));

    /// <summary>
    /// PHASE_12: every test written before the first-run wizard existed assumes the database
    /// already has a working credential — "unauthenticated" in those tests means "not signed
    /// in", not "this install has never been set up". Defaults to true so none of them need
    /// to change. xUnit's <c>IClassFixture</c> requires exactly one public constructor with
    /// no parameters, so this is a settable property rather than a constructor argument —
    /// set it via an object initializer immediately after construction, before the first
    /// call to <see cref="WebApplicationFactory{TEntryPoint}.CreateClient()"/> (which is what
    /// actually builds the host). Set false only for tests that specifically exercise the
    /// pristine, never-configured state (<c>FirstRunGateWebTests</c>, <c>FirstRunWizardTests</c>).
    /// </summary>
    public bool SeedCompletedSetup { get; init; } = true;

    public SyslogWebApplicationFactory() =>
        // Serve the test server over https so the Secure auth cookie is accepted by the
        // client's cookie container (PHASE_04: the cookie is Secure / HttpOnly / SameSite=Strict).
        ClientOptions.BaseAddress = new Uri("https://localhost");

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDirectory);
        builder.UseSetting("Collector:DataDirectory", _dataDirectory);
        builder.UseSetting("Data:DatabasePath", Path.Combine(_dataDirectory, "syslog.db"));
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        IHost host = base.CreateHost(builder);
        if (SeedCompletedSetup)
        {
            using IServiceScope scope = host.Services.CreateScope();
            var users = scope.ServiceProvider.GetRequiredService<SqliteUserStore>();
            var hasher = scope.ServiceProvider.GetRequiredService<IPasswordHasher>();
            UserAccount? admin = users.FindByUsernameAsync(DatabaseSeeder.SeededAdminUsername, CancellationToken.None)
                .GetAwaiter().GetResult();
            if (admin is not null && admin.PasswordHash is null)
            {
                users.SetPasswordAsync(
                        admin.UserId, hasher.Hash("not-used-directly-1"), mustChangePassword: false,
                        DateTimeOffset.UtcNow, CancellationToken.None)
                    .GetAwaiter().GetResult();
            }
        }

        return host;
    }

    protected override void Dispose(bool disposing)
    {
        base.Dispose(disposing);
        if (!disposing)
        {
            return;
        }

        SqliteConnection.ClearAllPools();
        try
        {
            Directory.Delete(_dataDirectory, recursive: true);
        }
        catch (IOException)
        {
        }
    }
}
