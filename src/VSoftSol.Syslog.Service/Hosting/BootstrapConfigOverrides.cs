using System.Text.Json;
using Microsoft.Extensions.Configuration;

namespace VSoftSol.Syslog.Service.Hosting;

/// <summary>
/// Bootstrap-tier settings the first-run wizard changes (PHASE_12 build item 2: listener
/// ports, the Web HTTPS port) live here rather than in the database — both hosts need them
/// before DI, and therefore before the database, exists (the same reason
/// <see cref="CollectorOptions.DataDirectory"/> is not a database setting). The file lives
/// inside the data directory, which both service accounts already have full control over
/// from the installer's ACLs, so the wizard (running as the Web service account) can write
/// it without any additional privilege. This is not the "hand-edited config file" CLAUDE.md
/// Constraint 7 rules out for normal operation — the wizard is what writes it; nobody opens
/// it in a text editor.
/// </summary>
public static class BootstrapConfigOverrides
{
    private const string FileName = "bootstrap-overrides.json";

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    public static string PathFor(string dataDirectory) => Path.Combine(dataDirectory, "config", FileName);

    /// <summary>Layers the override file, if one exists, on top of <paramref name="configuration"/>
    /// so it takes precedence over the compiled-in <c>appsettings.json</c> defaults. Safe to
    /// call on a fresh install where no override has ever been written.</summary>
    public static void Apply(IConfigurationBuilder configuration, string dataDirectory)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        configuration.AddJsonFile(PathFor(dataDirectory), optional: true, reloadOnChange: true);
    }

    public static async Task WriteAsync(
        string dataDirectory, int udpPort, int tcpPort, int webHttpsPort, CancellationToken cancellationToken)
    {
        string path = PathFor(dataDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);

        var document = new
        {
            Ingestion = new { UdpPort = udpPort, TcpPort = tcpPort },
            Kestrel = new { Endpoints = new { Https = new { Url = $"https://0.0.0.0:{webHttpsPort}" } } },
        };

        await using FileStream stream = File.Create(path);
        await JsonSerializer.SerializeAsync(stream, document, SerializerOptions, cancellationToken).ConfigureAwait(false);
    }
}
