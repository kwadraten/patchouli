using System.Xml.Linq;
using FluentAssertions;

namespace Patchouli.Tests;

public sealed class ProjectBoundaryTests
{
    [Fact]
    public void Core_does_not_reference_infrastructure_ui_or_mcp()
    {
        XDocument project =
            XDocument.Load(TestPaths.FromRepositoryRoot("src", "Patchouli.Core", "Patchouli.Core.csproj"));

        string[] referencedProjects = project
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        referencedProjects.Should().BeEmpty();
    }

    [Fact]
    public void Infrastructure_does_not_reference_ui()
    {
        XDocument project =
            XDocument.Load(TestPaths.FromRepositoryRoot("src", "Patchouli.Infrastructure",
                "Patchouli.Infrastructure.csproj"));

        string[] referencedProjects = project
            .Descendants("ProjectReference")
            .Select(element => element.Attribute("Include")?.Value ?? string.Empty)
            .ToArray();

        referencedProjects.Should()
            .NotContain(reference => reference.Contains("Patchouli.UI", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Ocr_provider_paths_do_not_insert_document_boxes_outside_shared_tree_service()
    {
        string ocrRoot = TestPaths.FromRepositoryRoot("src", "Patchouli.Infrastructure", "Ocr");
        string?[] offenders = Directory.EnumerateFiles(ocrRoot, "*.cs", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith("DocumentTreeService.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path =>
                File.ReadAllText(path).Contains("insert into document_boxes", StringComparison.OrdinalIgnoreCase))
            .Select(Path.GetFileName)
            .ToArray();

        offenders.Should()
            .BeEmpty("OCR providers and coordinators should delegate Box writes to DocumentTreeService.");
    }
}
