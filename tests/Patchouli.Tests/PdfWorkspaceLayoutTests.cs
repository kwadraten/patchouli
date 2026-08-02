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

    [Fact]
    public void PdfWorkspace_binds_edit_text_immediately_and_exposes_save_command()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "Views", "BoxEditorDialog.axaml"));
        string codeBehind = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "Views", "BoxEditorDialog.axaml.cs"));

        xaml.Should().Contain("UpdateSourceTrigger=PropertyChanged");
        codeBehind.Should().Contain("SaveTextCommand");
    }

    [Fact]
    public void PdfWorkspace_exposes_box_tree_edit_commands_and_media_payload_fields()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml"));
        string editorXaml = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "Views", "BoxEditorDialog.axaml"));
        string viewModel = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "ViewModels", "Ocr", "PdfBBoxViewModel.cs"));

        xaml.Should().Contain("SplitSelectedCommand")
            .And.Contain("MergeSelectedCommand")
            .And.Contain("MoveSelectedUpCommand")
            .And.Contain("MoveSelectedDownCommand")
            .And.Contain("IndentSelectedCommand")
            .And.Contain("OutdentSelectedCommand")
            .And.Contain("DeleteCommand")
            .And.Contain("ToggleSuppressedCommand")
            .And.Contain("TreeBoxes")
            .And.Contain("OnTreeExpandToggle")
            .And.Contain("OnPageNumberKeyDown");
        editorXaml.Should().Contain("AssetId");
        viewModel.Should().Contain("MediaBoxPayload(AssetId")
            .And.Contain("DeleteBoxAsync")
            .And.Contain("SetSuppressedAsync");
    }

    [Fact]
    public void PdfWorkspace_renders_paragraph_continuation_links_and_cross_page_badges()
    {
        string xaml =
            File.ReadAllText(TestPaths.FromRepositoryRoot("src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml"));
        string codeBehind = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml.cs"));
        string viewModel = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "ViewModels", "Ocr", "PdfWorkspaceViewModel.cs"));

        xaml.Should().Contain("x:Name=\"ContinuationItems\"")
            .And.Contain("ItemsSource=\"{Binding ContinuationLinks}\"")
            .And.Contain("x:Name=\"CrossPageContinuationItems\"")
            .And.Contain("Classes=\"continuationLink\"")
            .And.Contain("Classes=\"crossPageMark\"")
            .And.Contain("Classes.continuation=\"{Binding IsContinuation}\"")
            .And.Contain("跳转到续接源框")
            .And.Contain("JumpToContinuationSourceCommand");
        codeBehind.Should().Contain("OnCrossPageMarkPressed");
        viewModel.Should().Contain("UpdateContinuationLinksAsync")
            .And.Contain("JumpToContinuationSourceAsync");
    }

    [Fact]
    public void PdfWorkspace_preview_uses_page_render_service_and_disposes_pixel_lease()
    {
        string viewModel = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "ViewModels", "Ocr", "PdfWorkspaceViewModel.cs"));
        string contracts = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.Ocr", "PageRenderContracts.cs"));

        viewModel.Should().Contain("services.PageRenders.RenderPreviewAsync");
        viewModel.Should().Contain("using PdfPagePixelBufferLease raster = preview.Value");
        contracts.Should().Contain("class PdfPagePixelBufferLease : IDisposable");
    }

    [Fact]
    public void PdfWorkspace_prefetches_the_adjacent_window_with_generation_and_cancellation_guards()
    {
        string viewModel = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "ViewModels", "Ocr", "PdfWorkspaceViewModel.cs"));

        viewModel.Should().Contain("SchedulePrefetchAsync")
            .And.Contain("PrefetchPageAsync")
            .And.Contain("PrefetchWindow")
            .And.Contain("_prefetchCancellation?.Cancel()")
            .And.Contain("_lastNavigationDirection")
            .And.Contain("_renderGeneration")
            .And.Contain("preview.Value.Dispose()")
            .And.Contain("Pre-fetch must never affect the current page's success state");
    }

    [Fact]
    public void PdfWorkspace_prefetch_never_overwrites_the_current_page()
    {
        string viewModel = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "ViewModels", "Ocr", "PdfWorkspaceViewModel.cs"));

        viewModel.Should().NotContain("Image = preview.Value");
    }

    [Fact]
    public void PdfWorkspace_document_ocr_uses_background_queue_without_modal_operation()
    {
        string viewModel = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "ViewModels", "Ocr", "PdfWorkspaceViewModel.cs"));
        int start = viewModel.IndexOf("private async Task RunDocumentOcrAsync()", StringComparison.Ordinal);
        int end = viewModel.IndexOf("private async Task RunCurrentPageOcrAsync()", start, StringComparison.Ordinal);

        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);
        string method = viewModel[start..end];
        method.Should().Contain("QueueDocumentOcrAsync")
            .And.Contain("OcrQueue.ObserveQueue")
            .And.Contain("OcrQueuePriority.UserStartedDocument")
            .And.NotContain("RunOcrModalAsync")
            .And.NotContain("LogicalPageOcr.RunDocumentAsync");
    }

    [Fact]
    public void PdfWorkspace_native_preview_renders_markdown_without_debug_labels_and_links_selection()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml"));
        string viewModel = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "ViewModels", "Ocr", "PdfWorkspaceViewModel.cs"));

        xaml.Should().NotContain("<TextBlock Text=\"{Binding Kind}\"");
        xaml.Should().Contain("<controls:MarkdownTextBlock Markdown=\"{Binding Markdown}\" Block=\"{Binding Block}\"");
        xaml.Should().Contain("Text=\"复制 Markdown\"");
        xaml.Should().Contain("Command=\"{Binding CopyMarkdownCommand}\"");
        xaml.Should().Contain("Classes.selected=\"{Binding IsSelected}\"");
        xaml.Should().Contain("x:Name=\"PdfScrollViewer\"");
        xaml.Should().Contain("x:Name=\"PreviewScrollViewer\"");
        viewModel.Should().Contain("_previewSelectedBoxId = _selectedBox?.BoxId")
            .And.Contain("block.IsSelected = block.BoxId == _previewSelectedBoxId")
            .And.Contain("RunCurrentPageOcrCommand")
            .And.Contain("CopyMarkdownCommand")
            .And.Contain("LocalOcrSourceText")
            .And.NotContain("CandidateBoxes[0]");
    }

    [Fact]
    public void PdfWorkspace_overlaps_use_the_revision_keyed_lazy_projection_and_invalidate_on_edit()
    {
        string viewModel = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "ViewModels", "Ocr", "PdfWorkspaceViewModel.cs"));
        string detector = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.Core", "Documents", "DocumentBoxOverlap.cs"));

        viewModel.Should().Contain("services.Overlaps.GetOrCreateAsync")
            .And.Contain("DocumentBoxOverlapDetector.PolicyBasis")
            .And.Contain("Overlaps.Invalidate")
            .And.NotContain("DocumentBoxOverlapDetector.Detect(_loadedBoxes)");
        detector.Should().Contain("PolicyBasis");
    }

    [Fact]
    public void PdfWorkspace_overlap_projection_never_touches_the_source_file()
    {
        string viewModel = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "ViewModels", "Ocr", "PdfWorkspaceViewModel.cs"));

        int start = viewModel.IndexOf("UpdateOverlapWarningsAsync(", StringComparison.Ordinal);
        int end = viewModel.IndexOf("Raise(nameof(HasOverlapWarnings));", start, StringComparison.Ordinal);

        start.Should().BeGreaterThanOrEqualTo(0);
        end.Should().BeGreaterThan(start);
        string method = viewModel[start..end];
        method.Should().NotContain("PageRenders")
            .And.NotContain("SourceFingerprint")
            .And.NotContain("ResolveFile");
    }

    [Fact]
    public void PdfWorkspace_preview_selected_style_is_not_overridden_by_a_local_background()
    {
        string xaml = File.ReadAllText(TestPaths.FromRepositoryRoot(
            "src", "Patchouli.UI", "Views", "PdfWorkspacePage.axaml"));
        int start = xaml.IndexOf("<Button Classes=\"PreviewBlock\"", StringComparison.Ordinal);

        start.Should().BeGreaterThanOrEqualTo(0);
        string previewButton = xaml[start..xaml.IndexOf('>', start)];
        previewButton.Should().NotContain("Background=");
        xaml.Should().Contain("<Style Selector=\"Button.PreviewBlock.selected\">");
        xaml.Should().Contain("<Setter Property=\"BorderBrush\" Value=\"{DynamicResource SecondaryBrush}\" />");
    }
}
