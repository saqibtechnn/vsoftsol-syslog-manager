using System.Reflection;
using FluentAssertions;
using VSoftSol.Syslog.Core.Abstractions;
using VSoftSol.Syslog.Data.Scoping;
using VSoftSol.Syslog.Web.Security;
using Xunit;

namespace VSoftSol.Syslog.IntegrationTests.Auth;

/// <summary>
/// PHASE_04 build item 4: every UI data-read path goes through the one scope chokepoint
/// (<see cref="ScopedEventReader"/>). No type in the Web assembly may take a constructor
/// or method dependency on <see cref="ILogRepository"/> — that would let a page bypass scope.
/// </summary>
public sealed class ScopeChokepointArchitectureTests
{
    [Fact]
    public void NoWebType_DependsOnILogRepositoryDirectly()
    {
        Assembly web = typeof(PrincipalFactory).Assembly;
        var offenders = new List<string>();

        foreach (Type type in web.GetTypes())
        {
            foreach (ConstructorInfo ctor in type.GetConstructors(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (ctor.GetParameters().Any(p => p.ParameterType == typeof(ILogRepository)))
                {
                    offenders.Add($"{type.FullName}..ctor");
                }
            }

            foreach (FieldInfo field in type.GetFields(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance))
            {
                if (field.FieldType == typeof(ILogRepository))
                {
                    offenders.Add($"{type.FullName}.{field.Name}");
                }
            }
        }

        offenders.Should().BeEmpty("Web reads events only through ScopedEventReader");
    }

    [Fact]
    public void ScopedEventReader_ExposesTheFullReadSurface()
    {
        IEnumerable<string> methods = typeof(ScopedEventReader)
            .GetMethods(BindingFlags.Public | BindingFlags.Instance)
            .Select(m => m.Name);

        methods.Should().Contain(["GetByIdAsync", "QueryAsync", "CountAsync", "GetContextAsync"]);
    }
}
