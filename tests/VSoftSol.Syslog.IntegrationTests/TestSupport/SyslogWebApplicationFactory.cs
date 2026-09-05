using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.Configuration;

namespace VSoftSol.Syslog.IntegrationTests.TestSupport;

/// <summary>
/// Hosts the Web app with its data directory and database redirected to a throwaway temp
/// location, so the real ProgramData store is never touched by tests.
/// </summary>
public sealed class SyslogWebApplicationFactory : WebApplicationFactory<Program>
{
    private readonly string _dataDirectory =
        Path.Combine(Path.GetTempPath(), "vsoftsol-web-" + Guid.NewGuid().ToString("N"));

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        Directory.CreateDirectory(_dataDirectory);
        builder.UseSetting("Collector:DataDirectory", _dataDirectory);
        builder.UseSetting("Data:DatabasePath", Path.Combine(_dataDirectory, "syslog.db"));
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
