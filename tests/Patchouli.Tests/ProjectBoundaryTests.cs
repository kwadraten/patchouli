using System.Xml.Linq;
using FluentAssertions;

namespace Patchouli.Tests;

public sealed class ProjectBoundaryTests
{
    [Fact]
    public void Core_does_not_reference_infrastructure_ui_or_mcp()
    {
        var project = XDocument.Load(TestPaths.FromRepositoryRoot("src", "Patchouli.Core", "Patchouli.Core.csproj"));

        var referencedProjects = project
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        referencedProjects.Should().BeEmpty();
    }

    [Fact]
    public void Infrastructure_does_not_reference_ui()
    {
        var project = XDocument.Load(TestPaths.FromRepositoryRoot("src", "Patchouli.Infrastructure", "Patchouli.Infrastructure.csproj"));

        var referencedProjects = project
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        referencedProjects.Should().NotContain(reference => reference.Contains("Patchouli.UI", StringComparison.OrdinalIgnoreCase));
    }
}
