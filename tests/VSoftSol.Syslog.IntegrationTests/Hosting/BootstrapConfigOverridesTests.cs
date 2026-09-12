using FluentAssertions;
using Microsoft.Extensions.Configuration;
using VSoftSol.Syslog.Service.Hosting;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Hosting;

/// <summary>
/// PHASE_12 build item 2 (first-run wizard, listener ports step). Ports are bootstrap-tier
/// configuration (bound before DI / the database exist, same as
/// <see cref="CollectorOptions.DataDirectory"/>), so the wizard cannot simply write them to
/// the database like every other setting (CLAUDE.md Constraint 7's "one configuration
/// surface" is about runtime behaviour, not about settings the process needs before it can
/// even open the database). This is the file both hosts layer on top of their default
/// configuration, in the data directory both service accounts already have full control
/// over from the installer's ACLs — never a hand-edited file for normal operation, since the
/// wizard is what writes it.
/// </summary>
[Trait("Category", "Hosting")]
public sealed class BootstrapConfigOverridesTests
{
    private static string NewTempDataDir() => Path.Combine(Path.GetTempPath(), "vsoftsol-bootstraptest-" + Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task WriteAsync_ThenApply_TheIngestionPortsAreVisibleThroughIConfiguration()
    {
        string dataDir = NewTempDataDir();
        try
        {
            await BootstrapConfigOverrides.WriteAsync(dataDir, udpPort: 5514, tcpPort: 5515, webHttpsPort: 8443, CancellationToken.None);

            ConfigurationBuilder builder = new();
            BootstrapConfigOverrides.Apply(builder, dataDir);
            IConfigurationRoot configuration = builder.Build();

            configuration["Ingestion:UdpPort"].Should().Be("5514");
            configuration["Ingestion:TcpPort"].Should().Be("5515");
            configuration["Kestrel:Endpoints:Https:Url"].Should().Be("https://0.0.0.0:8443");
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public void Apply_NoOverrideFileWritten_DoesNotThrow()
    {
        string dataDir = NewTempDataDir();
        try
        {
            ConfigurationBuilder builder = new();
            Action act = () => BootstrapConfigOverrides.Apply(builder, dataDir);

            act.Should().NotThrow("a fresh install has no overrides yet; both hosts must start on their compiled-in defaults");
        }
        finally
        {
            // Apply() should not have created the directory or file as a side effect.
            Directory.Exists(dataDir).Should().BeFalse();
        }
    }

    [Fact]
    public async Task WriteAsync_CalledTwice_OverwritesRatherThanMerging()
    {
        string dataDir = NewTempDataDir();
        try
        {
            await BootstrapConfigOverrides.WriteAsync(dataDir, 5514, 5515, 8443, CancellationToken.None);
            await BootstrapConfigOverrides.WriteAsync(dataDir, 514, 514, 5443, CancellationToken.None);

            ConfigurationBuilder builder = new();
            BootstrapConfigOverrides.Apply(builder, dataDir);
            IConfigurationRoot configuration = builder.Build();

            configuration["Ingestion:UdpPort"].Should().Be("514");
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    /// <summary>
    /// v1.1 — live listener port changes. Unlike <see cref="BootstrapConfigOverrides.WriteAsync"/>
    /// (the first-run wizard's one-shot write of everything), a later, targeted port change
    /// from Settings must never clobber the Web HTTPS port the wizard already wrote — it
    /// merges into the existing file instead of replacing it wholesale.
    /// </summary>
    [Fact]
    public async Task UpdateIngestionPortsAsync_PreservesTheExistingWebHttpsPort()
    {
        string dataDir = NewTempDataDir();
        try
        {
            await BootstrapConfigOverrides.WriteAsync(dataDir, udpPort: 514, tcpPort: 514, webHttpsPort: 8443, CancellationToken.None);

            await BootstrapConfigOverrides.UpdateIngestionPortsAsync(dataDir, udpPort: 5514, tcpPort: 5515, CancellationToken.None);

            ConfigurationBuilder builder = new();
            BootstrapConfigOverrides.Apply(builder, dataDir);
            IConfigurationRoot configuration = builder.Build();

            configuration["Ingestion:UdpPort"].Should().Be("5514");
            configuration["Ingestion:TcpPort"].Should().Be("5515");
            configuration["Kestrel:Endpoints:Https:Url"].Should().Be("https://0.0.0.0:8443",
                "a Settings-page port change must not silently revert the wizard-configured Web HTTPS port");
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }

    [Fact]
    public async Task UpdateIngestionPortsAsync_NoExistingFile_CreatesOneWithJustThePorts()
    {
        string dataDir = NewTempDataDir();
        try
        {
            await BootstrapConfigOverrides.UpdateIngestionPortsAsync(dataDir, udpPort: 5514, tcpPort: 5515, CancellationToken.None);

            ConfigurationBuilder builder = new();
            BootstrapConfigOverrides.Apply(builder, dataDir);
            IConfigurationRoot configuration = builder.Build();

            configuration["Ingestion:UdpPort"].Should().Be("5514");
            configuration["Ingestion:TcpPort"].Should().Be("5515");
        }
        finally
        {
            Directory.Delete(dataDir, recursive: true);
        }
    }
}
