using System.ComponentModel;
using System.Reflection;
using Avalonia.Media;
using FluentAssertions;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Ocr;
using Patchouli.UI;
using Patchouli.UI.Services;
using Patchouli.UI.ViewModels;

namespace Patchouli.Tests;

[Collection("Avalonia")]
public sealed class PdfWorkspaceReactivityTests : IDisposable
{
    private readonly TemporaryAppSettingsFile _settings = new();

    public void Dispose()
    {
        _settings.Dispose();
    }

    private sealed class FakeClipboard : IClipboardService
    {
        public Task<string?> GetTextAsync()
        {
            return Task.FromResult<string?>(null);
        }

        public Task SetTextAsync(string text)
        {
            return Task.CompletedTask;
        }

        public Task ClearAsync()
        {
            return Task.CompletedTask;
        }
    }

    private MainWindowViewModel CreateMainWindow()
    {
        return new MainWindowViewModel(new FakeClipboard(), settingsPath: _settings.Path);
    }

    private PdfWorkspaceViewModel CreateWorkspace(MainWindowViewModel? main = null)
    {
        MainWindowViewModel vm = main ?? CreateMainWindow();
        LibraryItemViewModel item = new(
            ItemId.New().ToString(), "Title", "book", "", "", "", null, null, null, "source.pdf", "", 0, 0, "",
            _ => Task.CompletedTask, _ => Task.CompletedTask);
        return new PdfWorkspaceViewModel(vm, item);
    }

    private static PdfBBoxViewModel CreateBBox(
        PdfWorkspaceViewModel workspace,
        MainWindowViewModel main,
        double normX = 0.1,
        double normY = 0.2,
        double normW = 0.3,
        double normH = 0.4,
        string boxType = DocumentBoxType.Text,
        DocumentBoxId? continuesFrom = null)
    {
        DocumentBox box = new(
            DocumentTreeRevisionId.New(),
            DocumentBoxId.New(),
            DocumentInstanceId.New(),
            PageId.New(),
            null,
            null,
            boxType,
            null,
            null,
            new NormalizedBBox(normX, normY, normW, normH),
            new TextBoxPayload("Test content"),
            null,
            null,
            null,
            false,
            continuesFrom);
        return new PdfBBoxViewModel(main, workspace, box, 1000, 800, false);
    }

    private static void SetProperty<T>(object target, string propertyName, T value)
    {
        PropertyInfo prop = target.GetType().GetProperty(propertyName,
                                BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance)
                            ?? throw new ArgumentException($"Property {propertyName} not found on {target.GetType()}");
        MethodInfo setMethod = prop.GetSetMethod(true)
                               ?? throw new ArgumentException(
                                   $"Property {propertyName} has no setter on {target.GetType()}");
        setMethod.Invoke(target, [value]);
    }

    [Fact]
    public void Pure_derived_properties_update_automatically_when_source_properties_change()
    {
        PdfWorkspaceViewModel pdf = CreateWorkspace();

        // Zoom -> ZoomText
        pdf.AdjustZoom(0.5);
        pdf.ZoomText.Should().Be("150%");

        // PageIndex & PageCount -> PageNumberText & PageTotalText
        SetProperty(pdf, nameof(pdf.PageCount), 5);
        pdf.PageTotalText.Should().Be("/ 5");
        pdf.PageNumberText.Should().Be("1");

        SetProperty(pdf, nameof(pdf.PageIndex), 2);
        pdf.PageNumberText.Should().Be("3");

        // WidthPixels & HeightPixels -> ActualWidthPixels & ActualHeightPixels
        SetProperty(pdf, nameof(pdf.WidthPixels), 1920);
        SetProperty(pdf, nameof(pdf.HeightPixels), 1080);
        pdf.ActualWidthPixels.Should().Be(1920);
        pdf.ActualHeightPixels.Should().Be(1080);

        // ActiveTool -> IsSelectToolActive, IsMarqueeToolActive, IsCreateBoxToolActive, IsRectToolActive
        pdf.MarqueeToolCommand.Execute(null);
        pdf.IsSelectToolActive.Should().BeFalse();
        pdf.IsMarqueeToolActive.Should().BeTrue();
        pdf.IsCreateBoxToolActive.Should().BeFalse();
        pdf.IsRectToolActive.Should().BeTrue();

        pdf.CreateBoxToolCommand.Execute(null);
        pdf.IsSelectToolActive.Should().BeFalse();
        pdf.IsMarqueeToolActive.Should().BeFalse();
        pdf.IsCreateBoxToolActive.Should().BeTrue();
        pdf.IsRectToolActive.Should().BeTrue();

        pdf.SelectToolCommand.Execute(null);
        pdf.IsSelectToolActive.Should().BeTrue();
        pdf.IsRectToolActive.Should().BeFalse();

        // SourceValidationState & SourceWarning -> IsSourceValidating & HasSourceWarning
        SetProperty(pdf, nameof(pdf.SourceValidationState), SourceValidationStatus.Validating);
        pdf.IsSourceValidating.Should().BeTrue();
        pdf.HasSourceWarning.Should().BeFalse();

        SetProperty(pdf, nameof(pdf.SourceWarning), "source_changed");
        pdf.HasSourceWarning.Should().BeFalse(); // Still validating

        SetProperty(pdf, nameof(pdf.SourceValidationState), SourceValidationStatus.Changed);
        pdf.IsSourceValidating.Should().BeFalse();
        pdf.HasSourceWarning.Should().BeTrue();

        // IsHistoryTabActive -> SidebarTabTitle
        pdf.SidebarTabTitle.Should().Be("页面内容");
        SetProperty(pdf, nameof(pdf.IsHistoryTabActive), true);
        pdf.SidebarTabTitle.Should().Be("版本历史");

        // IsSidebarOpen -> SidebarMaxWidth, SidebarMinWidth
        pdf.SidebarMaxWidth.Should().Be(0.0);
        pdf.SidebarMinWidth.Should().Be(0.0);
        pdf.IsSidebarOpen = true;
        pdf.SidebarMaxWidth.Should().Be(800.0);
        pdf.SidebarMinWidth.Should().Be(200.0);

        // NewBoxType -> NewBoxIsTitle, NewBoxIsCode
        pdf.NewBoxType = DocumentBoxType.Title;
        pdf.NewBoxIsTitle.Should().BeTrue();
        pdf.NewBoxIsCode.Should().BeFalse();

        pdf.NewBoxType = DocumentBoxType.Code;
        pdf.NewBoxIsTitle.Should().BeFalse();
        pdf.NewBoxIsCode.Should().BeTrue();

        // PendingBBox -> IsNewBoxPending & SelectionVisible
        pdf.IsNewBoxPending.Should().BeFalse();
        SetProperty(pdf, nameof(pdf.SelectionWidth), 100.0);
        SetProperty(pdf, nameof(pdf.SelectionHeight), 50.0);
        SetProperty(pdf, nameof(pdf.PendingBBox), new NormalizedBBox(0.1, 0.1, 0.2, 0.2));
        pdf.IsNewBoxPending.Should().BeTrue();
        pdf.SelectionVisible.Should().BeTrue();

        SetProperty(pdf, nameof(pdf.PendingBBox), (NormalizedBBox?)null);
        pdf.IsNewBoxPending.Should().BeFalse();
        pdf.SelectionVisible.Should().BeFalse();
    }

    [Fact]
    public void BBox_coordinate_and_appearance_derived_properties_update_without_manual_refresh()
    {
        MainWindowViewModel main = CreateMainWindow();
        PdfWorkspaceViewModel workspace = CreateWorkspace(main);
        PdfBBoxViewModel bbox = CreateBBox(workspace, main, 0.1, 0.2, 0.3, 0.4);

        // Left, Top, Width, Height (ImageWidth=1000, ImageHeight=800)
        bbox.Left.Should().Be(100.0);
        bbox.Top.Should().Be(160.0);
        bbox.Width.Should().Be(300.0);
        bbox.Height.Should().Be(320.0);

        bbox.NormalizedX = 0.5;
        bbox.Left.Should().Be(500.0);

        bbox.NormalizedY = 0.6;
        bbox.Top.Should().Be(480.0);

        bbox.NormalizedWidth = 0.2;
        bbox.Width.Should().Be(200.0);

        bbox.NormalizedHeight = 0.1;
        bbox.Height.Should().Be(80.0);

        // BoxType -> BoxColor, VisualBoxColor, IsTitle, IsLogicalPage, IsMedia, IsCode
        bbox.BoxType = DocumentBoxType.Title;
        bbox.BoxColor.Should().Be(Brushes.Blue);
        bbox.VisualBoxColor.Should().Be(Brushes.Blue);
        bbox.IsTitle.Should().BeTrue();
        bbox.IsLogicalPage.Should().BeFalse();
        bbox.IsMedia.Should().BeFalse();
        bbox.IsCode.Should().BeFalse();

        bbox.BoxType = DocumentBoxType.LogicalPage;
        bbox.BoxColor.Should().Be(Brushes.Teal);
        bbox.VisualBoxColor.Should().Be(Brushes.Teal);
        bbox.IsTitle.Should().BeFalse();
        bbox.IsLogicalPage.Should().BeTrue();

        bbox.BoxType = DocumentBoxType.Image;
        bbox.BoxColor.Should().Be(Brushes.Red);
        bbox.IsMedia.Should().BeTrue();

        bbox.BoxType = DocumentBoxType.Code;
        bbox.BoxColor.Should().Be(Brushes.Gray);
        bbox.IsCode.Should().BeTrue();

        // IsSelected -> ZIndex
        bbox.IsSelected = true;
        bbox.ZIndex.Should().Be(1);
        bbox.IsSelected = false;
        bbox.ZIndex.Should().Be(0);

        // IsTreeExpanded -> TreeChevronAngle
        bbox.IsTreeExpanded = true;
        bbox.TreeChevronAngle.Should().Be(90);
        bbox.IsTreeExpanded = false;
        bbox.TreeChevronAngle.Should().Be(0);

        // Cross-object ShowHandles: IsSelected && Workspace.IsEditMode
        workspace.BoundingBoxes.Add(bbox);
        bbox.IsSelected = true;
        SetProperty(workspace, nameof(workspace.IsEditMode), false);
        bbox.ShowHandles.Should().BeFalse();

        SetProperty(workspace, nameof(workspace.IsEditMode), true);
        bbox.ShowHandles.Should().BeTrue();

        SetProperty(workspace, nameof(workspace.IsEditMode), false);
        bbox.ShowHandles.Should().BeFalse();
    }

    [Fact]
    public void Command_can_execute_invalidates_automatically_with_dependent_properties()
    {
        MainWindowViewModel main = CreateMainWindow();
        PdfWorkspaceViewModel pdf = CreateWorkspace(main);

        // ConfirmSplitCommand depends on EditSessionId and CanConfirmSplit
        int confirmSplitCanExecuteChangedCount = 0;
        pdf.ConfirmSplitCommand.CanExecuteChanged += (_, _) => confirmSplitCanExecuteChangedCount++;

        pdf.ConfirmSplitCommand.CanExecute(null).Should().BeFalse();
        SetProperty(pdf, nameof(pdf.EditSessionId), PageEditSessionId.New());
        confirmSplitCanExecuteChangedCount.Should().Be(1);
        pdf.ConfirmSplitCommand.CanExecute(null).Should().BeFalse();

        SetProperty(pdf, nameof(pdf.SplitFirstBBox), new NormalizedBBox(0, 0, 0.5, 1));
        confirmSplitCanExecuteChangedCount.Should().Be(2);
        pdf.CanConfirmSplit.Should().BeFalse();
        pdf.ConfirmSplitCommand.CanExecute(null).Should().BeFalse();

        SetProperty(pdf, nameof(pdf.SplitSecondBBox), new NormalizedBBox(0.5, 0, 0.5, 1));
        confirmSplitCanExecuteChangedCount.Should().Be(3);
        pdf.CanConfirmSplit.Should().BeTrue();
        pdf.ConfirmSplitCommand.CanExecute(null).Should().BeTrue();

        // When SplitSecondBBox is cleared, CanConfirmSplit becomes false -> command disabled
        SetProperty(pdf, nameof(pdf.SplitSecondBBox), (NormalizedBBox?)null);
        confirmSplitCanExecuteChangedCount.Should().Be(4);
        pdf.CanConfirmSplit.Should().BeFalse();
        pdf.ConfirmSplitCommand.CanExecute(null).Should().BeFalse();

        // Zoom commands clamp bounds
        pdf.ZoomInCommand.CanExecute(null).Should().BeTrue();
        pdf.ZoomOutCommand.CanExecute(null).Should().BeTrue();

        pdf.AdjustZoom(3.5); // Zoom clamped to 4.0
        pdf.Zoom.Should().Be(4.0);
        pdf.ZoomInCommand.CanExecute(null).Should().BeFalse();
        pdf.ZoomOutCommand.CanExecute(null).Should().BeTrue();

        pdf.AdjustZoom(-4.0); // Zoom clamped to 0.25
        pdf.Zoom.Should().Be(0.25);
        pdf.ZoomInCommand.CanExecute(null).Should().BeTrue();
        pdf.ZoomOutCommand.CanExecute(null).Should().BeFalse();

        // Page navigation boundaries
        SetProperty(pdf, nameof(pdf.PageIndex), 0);
        SetProperty(pdf, nameof(pdf.PageCount), 3);
        pdf.PreviousPageCommand.CanExecute(null).Should().BeFalse();
        pdf.NextPageCommand.CanExecute(null).Should().BeTrue();

        SetProperty(pdf, nameof(pdf.PageIndex), 1);
        pdf.PreviousPageCommand.CanExecute(null).Should().BeTrue();
        pdf.NextPageCommand.CanExecute(null).Should().BeTrue();

        SetProperty(pdf, nameof(pdf.PageIndex), 2);
        pdf.PreviousPageCommand.CanExecute(null).Should().BeTrue();
        pdf.NextPageCommand.CanExecute(null).Should().BeFalse();

        // While in edit mode, page navigation is disallowed
        SetProperty(pdf, nameof(pdf.IsEditMode), true);
        pdf.PreviousPageCommand.CanExecute(null).Should().BeFalse();
        pdf.NextPageCommand.CanExecute(null).Should().BeFalse();

        SetProperty(pdf, nameof(pdf.IsEditMode), false);
        pdf.PreviousPageCommand.CanExecute(null).Should().BeTrue();

        // InsertPendingBoxCommand
        pdf.InsertPendingBoxCommand.CanExecute(null).Should().BeFalse();
        SetProperty(pdf, nameof(pdf.PendingBBox), new NormalizedBBox(0.1, 0.1, 0.2, 0.2));
        pdf.InsertPendingBoxCommand.CanExecute(null).Should().BeTrue(); // EditSessionId was set above
        SetProperty(pdf, nameof(pdf.PendingBBox), (NormalizedBBox?)null);
        pdf.InsertPendingBoxCommand.CanExecute(null).Should().BeFalse();

        // AcceptLocalOcrCommand
        pdf.AcceptLocalOcrCommand.CanExecute(null).Should().BeFalse();
        SetProperty(pdf, nameof(pdf.LocalOcrCandidate), new OcrRegionCandidate(
            PageId.New(), new NormalizedBBox(0, 0, 1, 1), DocumentBoxType.Text,
            new TextBoxPayload("Ocr candidate"), null, 0.95));
        pdf.AcceptLocalOcrCommand.CanExecute(null).Should().BeTrue();
        SetProperty(pdf, nameof(pdf.LocalOcrCandidate), (OcrRegionCandidate?)null);
        pdf.AcceptLocalOcrCommand.CanExecute(null).Should().BeFalse();

        // SaveAndExitCommand & CancelEditModeCommand
        pdf.SaveAndExitCommand.CanExecute(null).Should().BeTrue();
        pdf.CancelEditModeCommand.CanExecute(null).Should().BeTrue();
        SetProperty(pdf, nameof(pdf.EditSessionId), (PageEditSessionId?)null);
        pdf.SaveAndExitCommand.CanExecute(null).Should().BeFalse();
        pdf.CancelEditModeCommand.CanExecute(null).Should().BeFalse();

        // PdfBBoxViewModel commands
        PdfBBoxViewModel normalBox = CreateBBox(pdf, main, boxType: DocumentBoxType.Text);
        pdf.BoundingBoxes.Add(normalBox);

        int saveBBoxCanExecuteChangedCount = 0;
        normalBox.SaveBBoxCommand.CanExecuteChanged += (_, _) => saveBBoxCanExecuteChangedCount++;

        normalBox.SaveBBoxCommand.CanExecute(null).Should().BeFalse();
        normalBox.DeleteCommand.CanExecute(null).Should().BeFalse();
        normalBox.SaveTextCommand.CanExecute(null).Should().BeFalse();

        SetProperty(pdf, nameof(pdf.IsEditMode), true);
        saveBBoxCanExecuteChangedCount.Should().Be(1);
        normalBox.SaveBBoxCommand.CanExecute(null).Should().BeTrue();
        normalBox.DeleteCommand.CanExecute(null).Should().BeTrue();
        normalBox.SaveTextCommand.CanExecute(null).Should().BeTrue();

        normalBox.BoxType = DocumentBoxType.LogicalPage;
        normalBox.SaveTextCommand.CanExecute(null).Should().BeFalse(); // LogicalPage cannot SaveText

        SetProperty(pdf, nameof(pdf.IsEditMode), false);
        saveBBoxCanExecuteChangedCount.Should().Be(2);
        normalBox.SaveBBoxCommand.CanExecute(null).Should().BeFalse();
        normalBox.DeleteCommand.CanExecute(null).Should().BeFalse();
        normalBox.SaveTextCommand.CanExecute(null).Should().BeFalse();

        // CandidateBoxes also receive NotifyEditModeChanged
        PdfBBoxViewModel candidateBox = CreateBBox(pdf, main, boxType: DocumentBoxType.Text);
        pdf.CandidateBoxes.Add(candidateBox);
        int candidateSaveBBoxChangedCount = 0;
        candidateBox.SaveBBoxCommand.CanExecuteChanged += (_, _) => candidateSaveBBoxChangedCount++;

        SetProperty(pdf, nameof(pdf.IsEditMode), true);
        candidateSaveBBoxChangedCount.Should().Be(1);
        candidateBox.SaveBBoxCommand.CanExecute(null).Should().BeTrue();

        SetProperty(pdf, nameof(pdf.IsEditMode), false);
        candidateSaveBBoxChangedCount.Should().Be(2);
        candidateBox.SaveBBoxCommand.CanExecute(null).Should().BeFalse();

        normalBox.JumpToContinuationSourceCommand.CanExecute(null).Should().BeFalse();
        PdfBBoxViewModel contBox = CreateBBox(pdf, main, continuesFrom: DocumentBoxId.New());
        contBox.JumpToContinuationSourceCommand.CanExecute(null).Should().BeTrue();
    }

    [Fact]
    public void Collection_mutations_project_automatically_to_flags_via_rx()
    {
        MainWindowViewModel main = CreateMainWindow();
        PdfWorkspaceViewModel pdf = CreateWorkspace(main);

        // BoundingBoxes -> HasNoBoundingBoxes
        pdf.HasNoBoundingBoxes.Should().BeTrue();
        PdfBBoxViewModel bbox = CreateBBox(pdf, main);
        pdf.BoundingBoxes.Add(bbox);
        pdf.HasNoBoundingBoxes.Should().BeFalse();

        pdf.BoundingBoxes.Remove(bbox);
        pdf.HasNoBoundingBoxes.Should().BeTrue();

        // PreviewBlocks -> HasNoPreviewBlocks
        pdf.HasNoPreviewBlocks.Should().BeTrue();
        MarkdownPreviewBlockViewModel block = new(
            DocumentBoxType.Text,
            "block content",
            new MarkdownBlock(DocumentBoxType.Text, "block content", 0, 13),
            0,
            DocumentBoxId.New(),
            () => Task.CompletedTask);
        pdf.PreviewBlocks.Add(block);
        pdf.HasNoPreviewBlocks.Should().BeFalse();

        pdf.PreviewBlocks.Remove(block);
        pdf.HasNoPreviewBlocks.Should().BeTrue();

        // PageRevisions -> HasPageRevisions
        pdf.HasPageRevisions.Should().BeFalse();
        DocumentTreeRevision rev = new(
            DocumentTreeRevisionId.New(),
            DocumentInstanceId.New(),
            PageId.New(),
            null,
            DocumentTreeRevisionSource.Import,
            DocumentTreeRevisionStatus.Committed,
            true,
            DateTimeOffset.UtcNow,
            DateTimeOffset.UtcNow);
        PageRevisionViewModel revision = new(rev, _ => Task.CompletedTask, _ => Task.CompletedTask);
        pdf.PageRevisions.Add(revision);
        pdf.HasPageRevisions.Should().BeTrue();

        pdf.PageRevisions.Remove(revision);
        pdf.HasPageRevisions.Should().BeFalse();
    }

    [Fact]
    public void Single_source_mutation_produces_single_notification_without_duplicate_raises()
    {
        MainWindowViewModel main = CreateMainWindow();
        PdfWorkspaceViewModel pdf = CreateWorkspace(main);

        Dictionary<string, int> changeCounts = new();
        pdf.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
            {
                changeCounts[e.PropertyName] = changeCounts.GetValueOrDefault(e.PropertyName) + 1;
            }
        };

        // 1. Mutate Zoom -> ZoomText should receive exactly 1 notification
        changeCounts.Clear();
        pdf.AdjustZoom(0.2);
        changeCounts.GetValueOrDefault(nameof(PdfWorkspaceViewModel.ZoomText)).Should().Be(1);

        // 2. Mutate ActiveTool -> IsSelectToolActive should receive exactly 1 notification
        changeCounts.Clear();
        pdf.MarqueeToolCommand.Execute(null);
        changeCounts.GetValueOrDefault(nameof(PdfWorkspaceViewModel.IsSelectToolActive)).Should().Be(1);
        changeCounts.GetValueOrDefault(nameof(PdfWorkspaceViewModel.IsMarqueeToolActive)).Should().Be(1);

        // 3. Mutate SourceValidationState -> IsSourceValidating should receive exactly 1 notification
        changeCounts.Clear();
        SetProperty(pdf, nameof(pdf.SourceValidationState), SourceValidationStatus.Validating);
        changeCounts.GetValueOrDefault(nameof(PdfWorkspaceViewModel.IsSourceValidating)).Should().Be(1);

        // 4. Mutate IsSidebarOpen -> SidebarMaxWidth should receive exactly 1 notification
        changeCounts.Clear();
        pdf.IsSidebarOpen = true;
        changeCounts.GetValueOrDefault(nameof(PdfWorkspaceViewModel.SidebarMaxWidth)).Should().Be(1);

        // 5. Mutate NewBoxType -> NewBoxIsTitle should receive exactly 1 notification
        changeCounts.Clear();
        pdf.NewBoxType = DocumentBoxType.Title;
        changeCounts.GetValueOrDefault(nameof(PdfWorkspaceViewModel.NewBoxIsTitle)).Should().Be(1);

        // 6. Mutate BBox.BoxType -> BoxColor should receive exactly 1 notification
        PdfBBoxViewModel bbox = CreateBBox(pdf, main);
        Dictionary<string, int> bboxChangeCounts = new();
        bbox.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not null)
            {
                bboxChangeCounts[e.PropertyName] = bboxChangeCounts.GetValueOrDefault(e.PropertyName) + 1;
            }
        };

        bbox.BoxType = DocumentBoxType.Title;
        bboxChangeCounts.GetValueOrDefault(nameof(PdfBBoxViewModel.BoxColor)).Should().Be(1);
        bboxChangeCounts.GetValueOrDefault(nameof(PdfBBoxViewModel.VisualBoxColor)).Should().Be(1);
        bboxChangeCounts.GetValueOrDefault(nameof(PdfBBoxViewModel.IsTitle)).Should().Be(1);

        // 7. Mutate BBox.NormalizedX -> Left should receive exactly 1 notification
        bboxChangeCounts.Clear();
        bbox.NormalizedX = 0.45;
        bboxChangeCounts.GetValueOrDefault(nameof(PdfBBoxViewModel.Left)).Should().Be(1);
    }
}
