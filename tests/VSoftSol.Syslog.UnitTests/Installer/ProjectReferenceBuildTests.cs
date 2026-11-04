using System.Xml.Linq;
using FluentAssertions;
using Xunit;

namespace VSoftSol.Syslog.UnitTests.Installer;

/// <summary>
/// A ProjectReference that carries its own build properties (<c>Properties</c> /
/// <c>AdditionalProperties</c> metadata) makes MSBuild build the referenced project a second time,
/// as a separate instance, in parallel with the normal build of the same project. Web used to
/// reference Service with <c>Properties=ExcludeAppSettingsFromPublish=true</c>, so a solution build
/// compiled Service twice at once (and, before Service dropped the property from its own
/// references, Core too, running the branding generator twice) and failed with CS2012 / file-lock
/// errors — on a clean CI checkout and intermittently on a developer machine. No reference in
/// the product may do this.
/// </summary>
public sealed class ProjectReferenceBuildTests
{
    private static readonly string[] BuildForkingMetadata =
        ["Properties", "AdditionalProperties", "SetConfiguration", "SetPlatform", "SetTargetFramework"];

    [Fact]
    public void ProductProjectReferences_CarryNoPerReferenceBuildProperties_SoEveryProjectBuildsOnce()
    {
        string[] projects = Directory
            .EnumerateFiles(Path.Combine(RepoPaths.Root, "src"), "*.csproj", SearchOption.AllDirectories)
            .ToArray();
        projects.Should().NotBeEmpty();

        List<string> offenders = [];
        foreach (string project in projects)
        {
            foreach (XElement reference in XDocument.Load(project).Descendants().Where(e => e.Name.LocalName == "ProjectReference"))
            {
                foreach (string name in BuildForkingMetadata)
                {
                    if (reference.Attribute(name) is not null || reference.Elements().Any(e => e.Name.LocalName == name))
                    {
                        offenders.Add($"{Path.GetFileName(project)} -> {(string?)reference.Attribute("Include")} sets {name}");
                    }
                }
            }
        }

        offenders.Should().BeEmpty("each of these makes MSBuild build the referenced project twice, concurrently");
    }

    [Fact]
    public void ServiceAppSettings_AreNotContentThatFlowsToProjectsReferencingService()
    {
        // Web references Service for the composition root only. Content with CopyToOutputDirectory /
        // CopyToPublishDirectory set would be copied into Web's output and collide with Web's own
        // appsettings.json, so Service copies its own file explicitly instead.
        XElement service = XDocument.Load(
            Path.Combine(RepoPaths.Root, "src", "VSoftSol.Syslog.Service", "VSoftSol.Syslog.Service.csproj")).Root!;

        XElement content = service.Descendants()
            .Single(e => e.Name.LocalName == "Content" && (string?)e.Attribute("Update") == "appsettings.json");

        ((string?)content.Parent!.Attribute("Condition")).Should().BeNull("a condition would need a per-reference build property to flip it");
        ((string?)content.Attribute("Condition")).Should().BeNull();
        ((string?)content.Attribute("CopyToOutputDirectory")).Should().Be("Never");
        ((string?)content.Attribute("CopyToPublishDirectory")).Should().Be("Never");
    }
}
