using FluentAssertions;

namespace Patchouli.Tests;

public sealed class PdfWorkspaceLayoutTests
{
    [Fact]
    public void PdfWorkspace_xaml_positions_bbox_item_containers_on_canvas()
    {
        string pdfXaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml"));

        pdfXaml.Should().Contain("<ItemsControl.ItemContainerTheme>");
        pdfXaml.Should().Contain("<ControlTheme TargetType=\"ContentPresenter\" x:DataType=\"vm:PdfBBoxViewModel\">");
        pdfXaml.Should().Contain("<Setter Property=\"Canvas.Left\" Value=\"{Binding Left}\" />");
        pdfXaml.Should().Contain("<Setter Property=\"Canvas.Top\" Value=\"{Binding Top}\" />");
        pdfXaml.Should().NotContain("Canvas.Left=\"{Binding Left}\"");
        pdfXaml.Should().NotContain("Canvas.Top=\"{Binding Top}\"");
    }

    [Fact]
    public void PdfWorkspace_uses_no_parallel_bbox_conflict_flyout_or_overwrite_action()
    {
        string xaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml"));
        string viewModel = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "ViewModels", "Ocr", "PdfWorkspaceViewModel.cs"));

        xaml.Should().NotContain("ResolveConflictOverwriteCommand").And.NotContain("强制覆盖");
        viewModel.Should().NotContain("ResolveConflictOverwriteAsync").And.NotContain("CheckOverlap");
    }

    [Fact]
    public void PdfWorkspace_sidebar_shows_centered_empty_state_for_empty_preview_and_box_list()
    {
        string xaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml"));

        xaml.Should().Contain("Text=\"空\"");
        xaml.Should().Contain("IsVisible=\"{Binding HasNoPreviewBlocks}\"");
        xaml.Should().Contain("IsVisible=\"{Binding HasNoBoundingBoxes}\"");
    }
}
