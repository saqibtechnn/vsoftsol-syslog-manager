using System.Reflection;
using FluentAssertions;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Architecture;

/// <summary>
/// The layer projects (Data, Ingestion, Rules, Reporting) may depend on Core and on
/// framework assemblies only — never on each other, never on Service or Web.
/// </summary>
public sealed class LayeringTests
{
    public static TheoryData<string, string[]> LayerRules => new()
    {
        {
            "VSoftSol.Syslog.Data",
            ["VSoftSol.Syslog.Ingestion", "VSoftSol.Syslog.Rules", "VSoftSol.Syslog.Reporting", "VSoftSol.Syslog.Service", "VSoftSol.Syslog.Web"]
        },
        {
            "VSoftSol.Syslog.Ingestion",
            ["VSoftSol.Syslog.Data", "VSoftSol.Syslog.Rules", "VSoftSol.Syslog.Reporting", "VSoftSol.Syslog.Service", "VSoftSol.Syslog.Web"]
        },
        {
            "VSoftSol.Syslog.Rules",
            ["VSoftSol.Syslog.Data", "VSoftSol.Syslog.Ingestion", "VSoftSol.Syslog.Reporting", "VSoftSol.Syslog.Service", "VSoftSol.Syslog.Web"]
        },
        {
            "VSoftSol.Syslog.Reporting",
            ["VSoftSol.Syslog.Ingestion", "VSoftSol.Syslog.Rules", "VSoftSol.Syslog.Service", "VSoftSol.Syslog.Web"]
        },
    };

    [Theory]
    [MemberData(nameof(LayerRules))]
    public void Layer_DoesNotReference_ForbiddenAssemblies(string layerAssemblyName, string[] forbidden)
    {
        Assembly layer = Assembly.Load(layerAssemblyName);

        IEnumerable<string> referenced = layer.GetReferencedAssemblies().Select(a => a.Name ?? string.Empty);

        referenced.Should().NotContain(forbidden);
    }

    [Fact]
    public void EveryLayer_References_Core()
    {
        foreach ((string layerName, _) in LayerRules.Select(row => ((string)row[0], (string[])row[1])))
        {
            Assembly.Load(layerName).GetReferencedAssemblies()
                .Select(a => a.Name)
                .Should().Contain("VSoftSol.Syslog.Core", "{0} is a layer over Core", layerName);
        }
    }
}
