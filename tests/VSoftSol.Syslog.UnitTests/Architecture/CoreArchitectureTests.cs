using System.Reflection;
using FluentAssertions;
using NetArchTest.Rules;
using VSoftSol.Syslog.Core.Events;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Architecture;

/// <summary>
/// Permanent fitness guards (CLAUDE.md "Dependency direction is strictly inward";
/// PHASE_00 "the architecture test fails if someone later adds an outward reference
/// from Core"). These assert compiled IL dependencies, not csproj references.
/// </summary>
public sealed class CoreArchitectureTests
{
    private static readonly Assembly CoreAssembly = typeof(SyslogEvent).Assembly;

    private static readonly string[] ForbiddenInCore =
    [
        "VSoftSol.Syslog.Data",
        "VSoftSol.Syslog.Ingestion",
        "VSoftSol.Syslog.Rules",
        "VSoftSol.Syslog.Reporting",
        "VSoftSol.Syslog.Service",
        "VSoftSol.Syslog.Web",
    ];

    [Fact]
    public void Core_HasNoDependencyOn_AnyOtherProductAssembly()
    {
        TestResult result = Types.InAssembly(CoreAssembly)
            .Should()
            .NotHaveDependencyOnAny(ForbiddenInCore)
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Core must reference nothing. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Core_HasNoDependencyOn_SystemDataOrSystemNet()
    {
        TestResult result = Types.InAssembly(CoreAssembly)
            .Should()
            .NotHaveDependencyOnAny("System.Data", "System.Net", "System.Net.Sockets", "System.Net.Http")
            .GetResult();

        result.IsSuccessful.Should().BeTrue(
            "Core does no I/O. Offenders: {0}",
            string.Join(", ", result.FailingTypeNames ?? []));
    }

    [Fact]
    public void Core_ReferencedAssemblies_AreFrameworkOnly()
    {
        IEnumerable<string> productRefs = CoreAssembly
            .GetReferencedAssemblies()
            .Select(a => a.Name ?? string.Empty)
            .Where(n => n.StartsWith("VSoftSol", StringComparison.Ordinal));

        productRefs.Should().BeEmpty("Core is the leaf of the dependency graph");
    }
}
