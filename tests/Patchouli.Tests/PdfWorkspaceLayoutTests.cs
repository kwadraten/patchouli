using FluentAssertions;

namespace Patchouli.Tests;

public sealed class PdfWorkspaceLayoutTests
{
    [Fact]
    public void PdfWorkspace_xaml_positions_bbox_item_containers_on_canvas()
    {
        var pdfXaml = File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml"));

        pdfXaml.Should().Contain("<ItemsControl.ItemContainerTheme>");
        pdfXaml.Should().Contain("<ControlTheme TargetType=\"ContentPresenter\">");
        pdfXaml.Should().Contain("<Setter Property=\"Canvas.Left\" Value=\"{Binding Left}\" />");
        pdfXaml.Should().Contain("<Setter Property=\"Canvas.Top\" Value=\"{Binding Top}\" />");
        pdfXaml.Should().NotContain("Canvas.Left=\"{Binding Left}\"");
        pdfXaml.Should().NotContain("Canvas.Top=\"{Binding Top}\"");
    }
}
