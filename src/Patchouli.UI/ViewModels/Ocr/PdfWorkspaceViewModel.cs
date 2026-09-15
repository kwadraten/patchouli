using System.Collections.Specialized;
using System.Reactive;
using System.Reactive.Concurrency;
using System.Reactive.Linq;
using System.Reactive.Subjects;
using Avalonia;
using Avalonia.Media;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Avalonia.Threading;
using Patchouli.Core.Credentials;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Ocr;
using System.Linq;
using Patchouli.Core.Results;
using Patchouli.UI.Diagnostics;
using Patchouli.UI.Services;
using CommunityToolkit.Mvvm.ComponentModel;
using Patchouli.UI;
using Patchouli.UI.Controls;
using Patchouli.UI.Reading;
using Patchouli.UI.ViewModels.Core;
using Patchouli.UI.ViewModels.Dialogs;
using Patchouli.Host.Composition;

namespace Patchouli.UI.ViewModels;

public sealed partial class PdfWorkspaceViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private bool _isConstructing = true;
    private int _lastNavigationDirection;
    private readonly Subject<PrefetchRequest> _prefetchSubject = new();

    private readonly record struct PrefetchRequest(
        HostServices? Services,
        DocumentInstanceId DocumentInstanceId,
        FileAssetId? FileAssetId,
        IReadOnlyList<int> Targets);

    private int _renderGeneration;

    /// <summary>Test seam: when set, intercepts preview page rendering requests so tests can
    /// simulate slow renders, cancellation, and concurrency without PDFium.</summary>
    internal Func<PageRenderRequest, CancellationToken, Task<Result<PdfPagePixelBufferLease>>>?
        PageRenderPreviewHandler { get; set; }

    private PdfBBoxViewModel? _selectedBox;
    private DocumentTreeRevisionId? _currentRevisionId;
    private DocumentTreeRevisionId? _draftRevisionId;
    private PageId? _currentPageId;
    private IReadOnlyList<DocumentBox> _loadedBoxes = [];
    private IReadOnlyList<Page> _pages = [];

    private readonly Dictionary<DocumentBoxId, (int PageIndex, DocumentBoxId HeadBoxId)>
        _crossPageContinuationSources = [];

    private readonly HashSet<DocumentBoxId> _collapsedBoxIds = [];
    private Point _selectionStartPoint;
    private DocumentBoxId? _localOcrTargetBoxId;
    private DocumentBoxId? _previewSelectedBoxId;
    private DocumentTreeRevisionId? _liveCurrentRevisionId;
    private CancellationTokenSource? _bookReadingCancellation;
    private IReadOnlyList<string>? _bookReadingFontFamilies;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageNumberText))]
    public partial int PageIndex { get; private set; }

    partial void OnPageIndexChanged(int value)
    {
        PreviousPageCommand?.NotifyCanExecuteChanged();
        NextPageCommand?.NotifyCanExecuteChanged();
    }

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(PageNumberText))]
    public partial int PageCount { get; private set; }

    partial void OnPageCountChanged(int value)
    {
        PreviousPageCommand?.NotifyCanExecuteChanged();
        NextPageCommand?.NotifyCanExecuteChanged();
    }

    [ObservableProperty] public partial int WidthPixels { get; private set; }
    [ObservableProperty] public partial int HeightPixels { get; private set; }

    [ObservableProperty] public partial PageEditSessionId? EditSessionId { get; private set; }

    partial void OnEditSessionIdChanged(PageEditSessionId? value)
    {
        InsertPendingBoxCommand?.NotifyCanExecuteChanged();
        ConfirmSplitCommand?.NotifyCanExecuteChanged();
        AcceptLocalOcrCommand?.NotifyCanExecuteChanged();
        SaveAndExitCommand?.NotifyCanExecuteChanged();
        CancelEditModeCommand?.NotifyCanExecuteChanged();
    }

    [ObservableProperty] public partial NormalizedBBox? PendingBBox { get; private set; }

    partial void OnPendingBBoxChanged(NormalizedBBox? value)
    {
        InsertPendingBoxCommand?.NotifyCanExecuteChanged();
    }

    [ObservableProperty] public partial DocumentBox? SplitSource { get; private set; }

    [ObservableProperty] public partial NormalizedBBox? SplitFirstBBox { get; private set; }

    partial void OnSplitFirstBBoxChanged(NormalizedBBox? value)
    {
        ConfirmSplitCommand?.NotifyCanExecuteChanged();
    }

    [ObservableProperty] public partial NormalizedBBox? SplitSecondBBox { get; private set; }

    partial void OnSplitSecondBBoxChanged(NormalizedBBox? value)
    {
        ConfirmSplitCommand?.NotifyCanExecuteChanged();
    }

    [ObservableProperty] public partial OcrRegionCandidate? LocalOcrCandidate { get; private set; }

    partial void OnLocalOcrCandidateChanged(OcrRegionCandidate? value)
    {
        AcceptLocalOcrCommand?.NotifyCanExecuteChanged();
    }

    [ObservableProperty] public partial string LocalOcrSourceText { get; private set; } = string.Empty;
    [ObservableProperty] public partial DocumentBox[] PendingMergeBoxes { get; private set; } = [];

    // Reading mode loads a window of pages around the current page and grows it on demand as the
    // reader scrolls. The session keeps every delivered page (so a view recreated on a tab switch
    // can replay it) and the set of already-loaded indices (so on-demand windows deduplicate).
    private readonly List<BookReadingPage> _bookReadingDelivered = new();
    private readonly HashSet<int> _bookReadingLoaded = new();
    private int[] _bookReadingIndices = [];
    private int _bookReadingStartIndex;
    private int _bookReadingLo;
    private int _bookReadingHi;
    private int _bookReadingPlannedLo;
    private int _bookReadingPlannedHi;
    private bool _bookReadingInitialWindowPending;
    private readonly Subject<Func<CancellationToken, Task>> _bookReadingSubject = new();
    private IBookReadingStream? _bookReadingStream;
    private DocumentInstanceId? _bookReadingDocumentInstanceId;

    public PdfWorkspaceViewModel(MainWindowViewModel main, LibraryItemViewModel item)
    {
        _main = main;
        Item = item;
        ReadingMediaLoader = new FileAssetMediaImageLoader(main);
        PreviousPageCommand = new AsyncCommand(PreviousPageAsync, () => !IsEditMode && PageIndex > 0);
        NextPageCommand =
            new AsyncCommand(NextPageAsync, () => !IsEditMode && PageCount > 0 && PageIndex < PageCount - 1);
        ReloadCommand = new AsyncCommand(ReloadAsync);
        ZoomInCommand = new AsyncCommand(() =>
        {
            SetZoom(Zoom + 0.1);
            return Task.CompletedTask;
        }, () => Zoom < 4.0);
        ZoomOutCommand = new AsyncCommand(() =>
        {
            SetZoom(Zoom - 0.1);
            return Task.CompletedTask;
        }, () => Zoom > 0.25);

        EnterEditModeCommand = new AsyncCommand(EnterEditModeAsync);
        SaveAndExitCommand = new AsyncCommand(SaveAndExitAsync, () => EditSessionId is not null);
        CancelEditModeCommand = new AsyncCommand(CancelEditModeAsync, () => EditSessionId is not null);
        SelectToolCommand = new AsyncCommand(() =>
        {
            SetActiveTool(PdfWorkspaceTool.Select);
            return Task.CompletedTask;
        });
        MarqueeToolCommand = new AsyncCommand(() =>
        {
            SetActiveTool(PdfWorkspaceTool.MarqueeSelect);
            return Task.CompletedTask;
        });
        CreateBoxToolCommand = new AsyncCommand(() =>
        {
            SetActiveTool(PdfWorkspaceTool.CreateBox);
            return Task.CompletedTask;
        });
        InsertPendingBoxCommand =
            new AsyncCommand(PersistDraftBoxAsync, () => EditSessionId is not null && PendingBBox is not null);
        CancelPendingBoxCommand = new AsyncCommand(() =>
        {
            ClearPendingBox();
            return Task.CompletedTask;
        });
        RunPendingOcrPrefillCommand = new AsyncCommand(RunPendingOcrPrefillAsync);
        SplitSelectedCommand = new AsyncCommand(SplitSelectedAsync);
        ConfirmSplitCommand = new AsyncCommand(ConfirmSplitAsync, () => EditSessionId is not null && CanConfirmSplit);
        CancelSplitCommand = new AsyncCommand(() =>
        {
            ClearSplit();
            return Task.CompletedTask;
        });
        RunLocalOcrCommand = new AsyncCommand(RunLocalOcrAsync);
        AcceptLocalOcrCommand = new AsyncCommand(AcceptLocalOcrAsync,
            () => EditSessionId is not null && LocalOcrCandidate is not null);
        RejectLocalOcrCommand = new AsyncCommand(() =>
        {
            ClearLocalOcrCandidate();
            Status = "已拒绝局部 OCR 候选结果；未写入识别记录或工作版本。";
            return Task.CompletedTask;
        });
        RunLogicalPageOcrCommand = new AsyncCommand(RunLogicalPageOcrAsync);
        RunCurrentPageOcrCommand = new AsyncCommand(RunCurrentPageOcrAsync);
        RunDocumentOcrCommand = new AsyncCommand(RunDocumentOcrAsync);
        CopyMarkdownCommand = new AsyncCommand(CopyMarkdownAsync);
        ShowContentTabCommand = new RelayCommand(_ => IsHistoryTabActive = false);
        ShowHistoryTabCommand = new RelayCommand(_ => IsHistoryTabActive = true);
        MergeSelectedCommand = new AsyncCommand(MergeSelectedAsync);
        ConfirmMergeCommand = new AsyncCommand(ConfirmMergeAsync);
        CancelMergeCommand = new AsyncCommand(() =>
        {
            ClearMerge();
            return Task.CompletedTask;
        });
        DeleteSelectedCommand = new AsyncCommand(DeleteSelectedAsync);
        MoveSelectedUpCommand = new AsyncCommand(() => MoveSelectedAsync(false));
        MoveSelectedDownCommand = new AsyncCommand(() => MoveSelectedAsync(true));
        IndentSelectedCommand = new AsyncCommand(IndentSelectedAsync);
        OutdentSelectedCommand = new AsyncCommand(OutdentSelectedAsync);
        ToggleSuppressedCommand = new AsyncCommand(ToggleSelectedSuppressedAsync);

        IObservable<Unit> boundingBoxesChanged = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => BoundingBoxes.CollectionChanged += h,
                h => BoundingBoxes.CollectionChanged -= h)
            .Select(_ => Unit.Default);

        boundingBoxesChanged
            .Select(_ => BoundingBoxes.Count == 0)
            .BindOutput(this, no => HasNoBoundingBoxes = no, ImmediateScheduler.Instance, null, true,
                BoundingBoxes.Count == 0);

        IObservable<Unit> previewBlocksChanged = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => PreviewBlocks.CollectionChanged += h,
                h => PreviewBlocks.CollectionChanged -= h)
            .Select(_ => Unit.Default);

        previewBlocksChanged
            .Select(_ => PreviewBlocks.Count == 0)
            .BindOutput(this, no => HasNoPreviewBlocks = no, ImmediateScheduler.Instance, null, true,
                PreviewBlocks.Count == 0);

        IObservable<Unit> pageRevisionsChanged = Observable
            .FromEventPattern<NotifyCollectionChangedEventHandler, NotifyCollectionChangedEventArgs>(
                h => PageRevisions.CollectionChanged += h,
                h => PageRevisions.CollectionChanged -= h)
            .Select(_ => Unit.Default);

        pageRevisionsChanged
            .Select(_ => PageRevisions.Count > 0)
            .BindOutput(this, has => HasPageRevisions = has, ImmediateScheduler.Instance, null, true,
                PageRevisions.Count > 0);

        Register(_prefetchSubject
            .Select(request => Observable.FromAsync(async cancellationToken =>
            {
                if (request.Services is null || request.Targets.Count == 0)
                {
                    return;
                }

                await PrefetchWindowAsync(request.Services, request.DocumentInstanceId, request.FileAssetId,
                    request.Targets, cancellationToken);
            }))
            .Switch()
            .Subscribe(
                _ => { },
                ex => UnexpectedExceptions.Sink.Report(ex, nameof(PdfWorkspaceViewModel), "PrefetchStream")));

        Register(_bookReadingSubject
            .Select(operation => Observable.FromAsync(operation))
            .Concat()
            .Subscribe(
                _ => { },
                ex => UnexpectedExceptions.Sink.Report(ex, nameof(PdfWorkspaceViewModel), "BookReadingStream")));

        EnterBookReadingCommand = new AsyncCommand(EnterBookReadingAsync);
        ExitBookReadingCommand = new RelayCommand(_ => ExitBookReading());
        BookReadingResetFontSizeCommand =
            new RelayCommand(_ => BookReadingFontSize = ReadingFontCatalog.DefaultFontSize);
        BookReadingFontSize = ReadingFontCatalog.ClampSize(_main.AppOptions.Ui.ReadingFontSize);
        BookReadingFontFamily = FamilyToDisplay(_main.AppOptions.Ui.ReadingFontFamily);
        _isConstructing = false;
    }

    [ObservableProperty] public partial Bitmap? Image { get; private set; }
    public LibraryItemViewModel Item { get; }
    public bool HasImage => Image is not null;
    public bool HasNoImage => Image is null;
    [ObservableProperty] public partial bool IsBusy { get; private set; }

    [ObservableProperty] public partial string Status { get; set; } = "选择题录后可预览 PDF。";

    partial void OnStatusChanged(string value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            if (value.Contains("失败", StringComparison.Ordinal) || value.Contains("不可用", StringComparison.Ordinal) ||
                value.StartsWith("ERROR", StringComparison.Ordinal))
            {
                _main.ReportError(value);
            }
            else
            {
                _main.Report(value);
            }
        }
    }

    [ExcludeFromDerivedGeneration] public string PageNumberText => PageCount == 0 ? "-" : (PageIndex + 1).ToString();
    public string PageTotalText => PageCount == 0 ? "/ -" : $"/ {PageCount}";
    [ExcludeFromDerivedGeneration] public string ZoomText => $"{Math.Round(Zoom * 100):0}%";

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(ZoomText))]
    public partial double Zoom { get; private set; } = 1.0;

    partial void OnZoomChanged(double value)
    {
        ZoomInCommand?.NotifyCanExecuteChanged();
        ZoomOutCommand?.NotifyCanExecuteChanged();
    }

    public double ActualWidthPixels => WidthPixels;
    public double ActualHeightPixels => HeightPixels;

    /// <summary>
    /// Last-known source validation state for the currently rendered page (AC16). It stays
    /// <see cref="SourceValidationStatus.Unverified"/> until a render lazily validates the
    /// source, transitions through <see cref="SourceValidationStatus.Validating"/> only while a
    /// render that may validate is in flight, and then settles on
    /// <see cref="SourceValidationStatus.Current"/>, <see cref="SourceValidationStatus.Changed"/>
    /// or <see cref="SourceValidationStatus.Unavailable"/>.
    /// </summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceWarning))]
    public partial string SourceValidationState { get; private set; } = SourceValidationStatus.Unverified;

    /// <summary>True only while a render that may lazily validate the source is in flight.</summary>
    public bool IsSourceValidating => SourceValidationState == SourceValidationStatus.Validating;

    /// <summary>Distinct source warning (e.g. source_changed/bbox_basis_stale) for the page.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSourceWarning))]
    public partial string? SourceWarning { get; private set; }

    /// <summary>True when the source warning is present and validation is not still running.</summary>
    [ExcludeFromDerivedGeneration]
    public bool HasSourceWarning => !string.IsNullOrWhiteSpace(SourceWarning) && !IsSourceValidating;

    [ObservableProperty] public partial bool IsEditMode { get; private set; }

    partial void OnIsEditModeChanged(bool value)
    {
        PreviousPageCommand?.NotifyCanExecuteChanged();
        NextPageCommand?.NotifyCanExecuteChanged();
        foreach (PdfBBoxViewModel box in BoundingBoxes)
        {
            box.NotifyEditModeChanged();
        }

        foreach (PdfBBoxViewModel box in CandidateBoxes)
        {
            box.NotifyEditModeChanged();
        }
    }

    /// <summary>True when the view-mode sidebar shows the version-history tab instead of the page preview.</summary>
    [ObservableProperty]
    public partial bool IsHistoryTabActive { get; private set; }

    public string SidebarTabTitle => IsHistoryTabActive ? "版本历史" : "页面内容";

    [ObservableProperty] public partial bool IsViewingHistoricalRevision { get; private set; }

    public IReadOnlyList<string> NewBoxTypeOptions { get; } =
    [
        DocumentBoxType.Text, DocumentBoxType.Title, DocumentBoxType.RefText, DocumentBoxType.List,
        DocumentBoxType.Table, DocumentBoxType.Code, DocumentBoxType.Equation, DocumentBoxType.LogicalPage,
        DocumentBoxType.Image, DocumentBoxType.Chart
    ];

    public IReadOnlyList<string> EditableBoxTypeOptions { get; } =
    [
        DocumentBoxType.Text, DocumentBoxType.Title, DocumentBoxType.RefText, DocumentBoxType.Equation,
        DocumentBoxType.List, DocumentBoxType.Image, DocumentBoxType.Table, DocumentBoxType.Chart,
        DocumentBoxType.Code, DocumentBoxType.ImageCaption, DocumentBoxType.ImageFootnote,
        DocumentBoxType.TableCaption, DocumentBoxType.TableFootnote, DocumentBoxType.ChartCaption,
        DocumentBoxType.ChartFootnote, DocumentBoxType.CodeCaption, DocumentBoxType.CodeFootnote,
        DocumentBoxType.Header, DocumentBoxType.Footer, DocumentBoxType.PageNumber, DocumentBoxType.AsideText,
        DocumentBoxType.PageFootnote
    ];

    public bool IsNewBoxPending => PendingBBox is not null;
    public bool IsSplitPending => SplitSource is not null;
    public bool CanConfirmSplit => SplitFirstBBox is not null && SplitSecondBBox is not null;

    public string SplitStepText => SplitFirstBBox is null
        ? "请在页面框出第一个替代区域。"
        : SplitSecondBBox is null
            ? "请在页面框出第二个替代区域。"
            : "两个区域已就绪；检查两份内容后确认拆分。";

    public bool HasCandidate => LocalOcrCandidate is not null;
    public bool IsMergePending => PendingMergeBoxes.Length > 0;

    [ObservableProperty] public partial string MergeText { get; set; } = string.Empty;

    public string OcrPresetStatusText => "局部与页面 OCR 使用当前库的 MinerU preset。";

    [ObservableProperty] public partial string SplitFirstText { get; set; } = string.Empty;

    [ObservableProperty] public partial string SplitSecondText { get; set; } = string.Empty;

    [ObservableProperty] public partial string NewBoxText { get; set; } = string.Empty;

    [ObservableProperty] public partial string NewBoxType { get; set; } = DocumentBoxType.Text;

    public bool NewBoxIsTitle => NewBoxType == DocumentBoxType.Title;
    public bool NewBoxIsCode => NewBoxType is DocumentBoxType.Code or DocumentBoxType.Algorithm;

    [ObservableProperty] public partial int NewHeadingLevel { get; set; } = 1;

    partial void OnNewHeadingLevelChanged(int value)
    {
        int clamped = Math.Clamp(value, 1, 6);
        if (value != clamped)
        {
            NewHeadingLevel = clamped;
        }
    }

    [ObservableProperty] public partial string NewCodeLanguage { get; set; } = string.Empty;

    [ObservableProperty] public partial bool IsSidebarOpen { get; set; }

    public double SidebarMaxWidth => IsSidebarOpen ? 800.0 : 0.0;
    public double SidebarMinWidth => IsSidebarOpen ? 200.0 : 0.0;

    [ObservableProperty] public partial PdfWorkspaceTool ActiveTool { get; private set; } = PdfWorkspaceTool.Select;

    public bool IsSelectToolActive => ActiveTool == PdfWorkspaceTool.Select;
    public bool IsMarqueeToolActive => ActiveTool == PdfWorkspaceTool.MarqueeSelect;
    public bool IsCreateBoxToolActive => ActiveTool == PdfWorkspaceTool.CreateBox;
    public bool IsRectToolActive => ActiveTool != PdfWorkspaceTool.Select;

    private void SetActiveTool(PdfWorkspaceTool tool)
    {
        ActiveTool = tool;
    }

    [ObservableProperty] public partial bool IsDrawing { get; private set; }

    [ObservableProperty] public partial double SelectionLeft { get; private set; }

    [ObservableProperty] public partial double SelectionTop { get; private set; }

    [ObservableProperty] public partial double SelectionWidth { get; private set; }

    [ObservableProperty] public partial double SelectionHeight { get; private set; }

    public bool SelectionVisible => (IsDrawing || IsNewBoxPending) && SelectionWidth > 0 && SelectionHeight > 0;

    public System.Collections.ObjectModel.ObservableCollection<PdfBBoxViewModel> BoundingBoxes { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<PdfBBoxViewModel> SelectedBoxes { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<SplitDraftBoxViewModel> SplitDraftBoxes { get; } =
        new();

    public System.Collections.ObjectModel.ObservableCollection<MarkdownPreviewBlockViewModel> PreviewBlocks { get; } =
        new();

    public System.Collections.ObjectModel.ObservableCollection<PdfBBoxViewModel> CandidateBoxes { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<PageRevisionViewModel> PageRevisions { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<PdfOverlapMarkerViewModel> OverlapMarkers { get; } =
        new();

    public System.Collections.ObjectModel.ObservableCollection<PdfContinuationLinkViewModel>
        ContinuationLinks { get; } =
        new();

    public System.Collections.ObjectModel.ObservableCollection<PdfCrossPageContinuationViewModel>
        CrossPageContinuationMarkers { get; } = new();

    // Visible rows of the edit-mode box tree: BoundingBoxes minus collapsed subtrees.
    public System.Collections.ObjectModel.ObservableCollection<PdfBBoxViewModel> TreeBoxes { get; } = new();

    [ObservableProperty] public partial bool HasNoBoundingBoxes { get; private set; } = true;
    [ObservableProperty] public partial bool HasPageRevisions { get; private set; }
    [ObservableProperty] public partial bool HasNoPreviewBlocks { get; private set; } = true;

    // Render-ready snapshot of the page content; the sidebar's reading view draws this instead of
    // the per-block MarkdownPreviewBlockViewModel list.
    [ObservableProperty] public partial DocumentReadingScene? ReadingScene { get; private set; }

    [ObservableProperty] public partial DocumentBoxId? ReadingSelectedBoxId { get; private set; }

    // Resolves the reading view's media block asset ids to decoded images (see the loader type).
    public IMediaImageLoader ReadingMediaLoader { get; }

    public bool HasOverlapWarnings => OverlapMarkers.Count > 0;
    public bool HasContinuationLinks => ContinuationLinks.Count > 0;
    public bool HasCrossPageContinuationMarkers => CrossPageContinuationMarkers.Count > 0;
    public bool IsSingleSelection => SelectedBoxes.Count <= 1;
    public bool IsMultiSelection => SelectedBoxes.Count > 1;

    public PdfBBoxViewModel? SelectedBox
    {
        get => _selectedBox;
        set => ApplySelection(value is null ? [] : [value], false);
    }

    public void SelectBox(PdfBBoxViewModel box, bool additive)
    {
        ApplySelection([box], additive);
    }

    internal void SelectOverlapPair(PdfOverlapMarkerViewModel marker)
    {
        ApplySelection([marker.First, marker.Second], false);
    }

    internal void ToggleTreeExpansion(PdfBBoxViewModel box)
    {
        if (!_collapsedBoxIds.Remove(box.BoxId))
        {
            _collapsedBoxIds.Add(box.BoxId);
        }

        box.IsTreeExpanded = !_collapsedBoxIds.Contains(box.BoxId);
        RebuildTreeBoxes();
    }

    private void RebuildTreeBoxes()
    {
        TreeBoxes.Clear();
        int skipBelowDepth = -1;
        foreach (PdfBBoxViewModel box in BoundingBoxes)
        {
            if (skipBelowDepth >= 0 && box.Depth >= skipBelowDepth)
            {
                continue;
            }

            skipBelowDepth = -1;
            TreeBoxes.Add(box);
            if (box.HasChildren && !box.IsTreeExpanded)
            {
                skipBelowDepth = box.Depth + 1;
            }
        }
    }

    public void ClearSelection()
    {
        SelectedBox = null;
    }

    private void ApplySelection(IReadOnlyList<PdfBBoxViewModel> boxes, bool additive)
    {
        if (additive)
        {
            foreach (PdfBBoxViewModel box in boxes)
            {
                if (SelectedBoxes.Remove(box))
                {
                    box.IsSelected = false;
                }
                else
                {
                    SelectedBoxes.Add(box);
                    box.IsSelected = true;
                }
            }
        }
        else
        {
            foreach (PdfBBoxViewModel old in SelectedBoxes)
            {
                old.IsSelected = false;
            }

            SelectedBoxes.Clear();
            foreach (PdfBBoxViewModel box in boxes)
            {
                if (!SelectedBoxes.Contains(box))
                {
                    SelectedBoxes.Add(box);
                    box.IsSelected = true;
                }
            }
        }

        SetPrimaryBox(SelectedBoxes.Count > 0 ? SelectedBoxes[^1] : null);
        RaiseSelectionFlags();
    }

    private void SetPrimaryBox(PdfBBoxViewModel? value)
    {
        if (_selectedBox == value)
        {
            return;
        }

        _selectedBox = value;
        if (_selectedBox is null || !_selectedBox.IsSuppressed)
        {
            _previewSelectedBoxId = _selectedBox?.BoxId;
        }

        foreach (MarkdownPreviewBlockViewModel block in PreviewBlocks)
        {
            block.IsSelected = block.BoxId == _previewSelectedBoxId;
        }

        ReadingSelectedBoxId = _previewSelectedBoxId;
        Raise(nameof(SelectedBox));
    }

    public AsyncCommand PreviousPageCommand { get; }
    public AsyncCommand NextPageCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand ZoomInCommand { get; }
    public AsyncCommand ZoomOutCommand { get; }
    public AsyncCommand EnterEditModeCommand { get; }
    public AsyncCommand SaveAndExitCommand { get; }
    public AsyncCommand CancelEditModeCommand { get; }
    public AsyncCommand SelectToolCommand { get; }
    public AsyncCommand MarqueeToolCommand { get; }
    public AsyncCommand CreateBoxToolCommand { get; }
    public AsyncCommand InsertPendingBoxCommand { get; }
    public AsyncCommand CancelPendingBoxCommand { get; }
    public AsyncCommand RunPendingOcrPrefillCommand { get; }
    public AsyncCommand SplitSelectedCommand { get; }
    public AsyncCommand ConfirmSplitCommand { get; }
    public AsyncCommand CancelSplitCommand { get; }
    public AsyncCommand RunLocalOcrCommand { get; }
    public AsyncCommand AcceptLocalOcrCommand { get; }
    public AsyncCommand RejectLocalOcrCommand { get; }
    public AsyncCommand RunLogicalPageOcrCommand { get; }
    public AsyncCommand RunCurrentPageOcrCommand { get; }
    public AsyncCommand RunDocumentOcrCommand { get; }
    public AsyncCommand CopyMarkdownCommand { get; }
    public RelayCommand ShowContentTabCommand { get; }
    public RelayCommand ShowHistoryTabCommand { get; }
    public AsyncCommand MergeSelectedCommand { get; }
    public AsyncCommand ConfirmMergeCommand { get; }
    public AsyncCommand CancelMergeCommand { get; }
    public AsyncCommand DeleteSelectedCommand { get; }
    public AsyncCommand MoveSelectedUpCommand { get; }
    public AsyncCommand MoveSelectedDownCommand { get; }
    public AsyncCommand IndentSelectedCommand { get; }
    public AsyncCommand OutdentSelectedCommand { get; }
    public AsyncCommand ToggleSuppressedCommand { get; }

    public void OnPointerPressed(double x, double y)
    {
        if (ActiveTool == PdfWorkspaceTool.Select)
        {
            return;
        }

        if (ActiveTool == PdfWorkspaceTool.CreateBox && !IsEditMode)
        {
            return;
        }

        _selectionStartPoint = new Point(x, y);
        SelectionLeft = x;
        SelectionTop = y;
        SelectionWidth = 0;
        SelectionHeight = 0;
        IsDrawing = true;
    }

    public void OnPointerMoved(double x, double y)
    {
        if (!IsDrawing)
        {
            return;
        }

        Point currentPoint = new(x, y);
        SelectionLeft = Math.Min(_selectionStartPoint.X, currentPoint.X);
        SelectionTop = Math.Min(_selectionStartPoint.Y, currentPoint.Y);
        SelectionWidth = Math.Max(_selectionStartPoint.X, currentPoint.X) - SelectionLeft;
        SelectionHeight = Math.Max(_selectionStartPoint.Y, currentPoint.Y) - SelectionTop;
    }

    public void OnPointerReleased(bool additive)
    {
        if (!IsDrawing)
        {
            return;
        }

        IsDrawing = false;
        if (SelectionWidth > 5 && SelectionHeight > 5)
        {
            if (ActiveTool == PdfWorkspaceTool.MarqueeSelect)
            {
                ApplyMarqueeSelection(additive);
                SetActiveTool(PdfWorkspaceTool.Select);
            }
            else
            {
                CreateBBoxFromSelection();
            }
        }

        if (PendingBBox is null)
        {
            SelectionWidth = 0;
            SelectionHeight = 0;
        }
    }

    private void ApplyMarqueeSelection(bool additive)
    {
        if (WidthPixels <= 0 || HeightPixels <= 0)
        {
            return;
        }

        double x = SelectionLeft / WidthPixels;
        double y = SelectionTop / HeightPixels;
        double width = SelectionWidth / WidthPixels;
        double height = SelectionHeight / HeightPixels;
        List<PdfBBoxViewModel> hits = BoundingBoxes.Where(box =>
            (!box.IsLogicalPage ||
             (box.NormalizedX >= x && box.NormalizedY >= y &&
              box.NormalizedX + box.NormalizedWidth <= x + width &&
              box.NormalizedY + box.NormalizedHeight <= y + height)) &&
            box.NormalizedX < x + width && box.NormalizedX + box.NormalizedWidth > x &&
            box.NormalizedY < y + height && box.NormalizedY + box.NormalizedHeight > y).ToList();
        if (additive)
        {
            foreach (PdfBBoxViewModel hit in hits)
            {
                if (!SelectedBoxes.Contains(hit))
                {
                    SelectedBoxes.Add(hit);
                    hit.IsSelected = true;
                }
            }

            if (SelectedBoxes.Count > 0)
            {
                SetPrimaryBox(SelectedBoxes[^1]);
            }
        }
        else
        {
            ApplySelection(hits, false);
        }

        RaiseSelectionFlags();
    }

    private void RaiseSelectionFlags()
    {
        Raise(nameof(IsSingleSelection));
        Raise(nameof(IsMultiSelection));
    }

    private void CreateBBoxFromSelection()
    {
        if (_draftRevisionId is null || _currentPageId is null || WidthPixels <= 0 || HeightPixels <= 0)
        {
            Status = "请先进入编辑模式并加载页面。";
            return;
        }

        double x = Math.Clamp(SelectionLeft / WidthPixels, 0, 1);
        double y = Math.Clamp(SelectionTop / HeightPixels, 0, 1);
        double width = Math.Clamp(SelectionWidth / WidthPixels, 0.0001, 1 - x);
        double height = Math.Clamp(SelectionHeight / HeightPixels, 0.0001, 1 - y);
        if (SplitSource is not null)
        {
            if (SplitFirstBBox is null)
            {
                SplitFirstBBox = new NormalizedBBox(x, y, width, height);
                SplitDraftBoxes.Add(new SplitDraftBoxViewModel(
                    x * WidthPixels, y * HeightPixels, width * WidthPixels, height * HeightPixels, "1"));
            }
            else
            {
                SplitSecondBBox = new NormalizedBBox(x, y, width, height);
                SplitDraftBoxes.Add(new SplitDraftBoxViewModel(
                    x * WidthPixels, y * HeightPixels, width * WidthPixels, height * HeightPixels, "2"));
            }

            Status = SplitStepText;
            return;
        }

        PendingBBox = new NormalizedBBox(x, y, width, height);
        NewBoxText = string.Empty;
        NewBoxType = DocumentBoxType.Text;
        NewHeadingLevel = 1;
        NewCodeLanguage = string.Empty;
        Status = _loadedBoxes.Count == 0
            ? "填写类型和内容后插入第一个根边界框。"
            : "填写类型和内容后插入到边界框列表末尾。";
        OpenNewBoxEditorAsync().Observe(nameof(PdfWorkspaceViewModel), nameof(OpenNewBoxEditorAsync));
    }

    private async Task PersistDraftBoxAsync()
    {
        if (EditSessionId is null || _draftRevisionId is null || _currentPageId is null)
        {
            return;
        }

        if (PendingBBox is null)
        {
            return;
        }

        DocumentBoxId? after = OrderSiblings(_loadedBoxes.Where(box => box.ParentBoxId is null))
            .LastOrDefault()?.BoxId;
        IDocumentTreeEditor editor = (await _main.ServicesAsync()).DocumentTreeEditor;
        Result<DocumentBox> result;
        if (NewBoxType == DocumentBoxType.LogicalPage)
        {
            if (_loadedBoxes.Any(box => box.ParentBoxId is null && box.BoxType != DocumentBoxType.LogicalPage))
            {
                Status = "普通根边界框不能自动转换为逻辑页；请在草稿中先显式移除或重建直属内容。";
                return;
            }

            result = await editor.InsertLogicalPageAsync(EditSessionId.Value, after, PendingBBox.Value);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(NewBoxText))
            {
                Status = "新建叶子边界框必须填写有效内容。";
                return;
            }

            result = await editor.DrawAndInsertLeafAsync(
                EditSessionId.Value,
                new InsertLeafCommand(null, after, NewBoxType, null, null, PendingBBox.Value,
                    CreatePayload(NewBoxType, NewBoxText),
                    NewBoxType == DocumentBoxType.Title ? NewHeadingLevel : null,
                    NewBoxType == DocumentBoxType.Code && !string.IsNullOrWhiteSpace(NewCodeLanguage)
                        ? NewCodeLanguage.Trim()
                        : null));
        }

        if (result.IsFailure)
        {
            Status = $"新增边界框未写入草稿：{result.ErrorMessage}";
            return;
        }

        await RefreshBoxesAsync();
        SelectedBox = BoundingBoxes.FirstOrDefault(box => box.BoxId == result.Value.BoxId);
        ClearPendingBox();
        await RefreshPreviewAsync();
        Status = "已创建边界框；右侧文本保存到页面草稿，提交前不会影响当前版本。";
    }

    private static DocumentBoxPayload CreatePayload(string boxType, string text, DocumentBoxPayload? existing = null)
    {
        return boxType switch
        {
            DocumentBoxType.List => new ListBoxPayload(text),
            DocumentBoxType.Table => new TableBoxPayload(text),
            DocumentBoxType.Code => new CodeBoxPayload(text),
            DocumentBoxType.Equation => new EquationBoxPayload(text),
            DocumentBoxType.Image or DocumentBoxType.Chart => new MediaBoxPayload(
                (existing as MediaBoxPayload)?.AssetId, string.IsNullOrWhiteSpace(text) ? null : text),
            _ => new TextBoxPayload(text)
        };
    }

    private async Task<OcrPresetId?> ResolveOcrPresetIdAsync(OcrScope scope)
    {
        string engineId = _main.AppOptions.OcrEngines.EngineFor(scope);
        Result<OcrPresetId> preset =
            await (await _main.ImportOrchestratorAsync()).EnsurePresetForEngineAsync(engineId);
        if (preset.IsFailure)
        {
            Status = $"OCR preset 不可用：{preset.ErrorMessage}";
            return null;
        }

        return preset.Value;
    }

    private async Task RunOcrModalAsync(string title, string initialStatus, Func<Task<Result>> operation)
    {
        await _main.ModalOperations.RunAsync(
            new ModalOperationOptions(title, initialStatus),
            async _ => await Dispatcher.UIThread.InvokeAsync(operation));
    }

    private async Task RunPendingOcrPrefillAsync()
    {
        if (PendingBBox is null || _currentPageId is null || string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
        {
            Status = "请先框出区域，再运行 OCR 预填。";
            return;
        }

        OcrPresetId? presetId = await ResolveOcrPresetIdAsync(OcrScope.Region);
        if (presetId is null)
        {
            return;
        }

        await RunOcrModalAsync("局部 OCR 预填", "正在识别所选区域...", async () =>
        {
            Result<OcrRegionCandidate> candidate = await (await _main.ServicesAsync()).Ocr
                .RecognizeRegionCandidateAsync(
                    DocumentInstanceId.Parse(Item.DocumentInstanceId), presetId.Value, _currentPageId.Value,
                    PendingBBox.Value);
            if (candidate.IsFailure)
            {
                Status = $"局部 OCR 预填失败：{candidate.ErrorMessage}";
                return Result.Failure(candidate.ErrorCode!, candidate.ErrorMessage!);
            }

            NewBoxType = candidate.Value.BoxType;
            NewBoxText = PayloadTextFor(new DocumentBox(
                _draftRevisionId ?? _currentRevisionId ?? DocumentTreeRevisionId.New(),
                DocumentBoxId.New(),
                DocumentInstanceId.Parse(Item.DocumentInstanceId),
                candidate.Value.PageId,
                null,
                null,
                candidate.Value.BoxType,
                null,
                null,
                candidate.Value.BBox,
                candidate.Value.Payload,
                candidate.Value.HeadingLevel,
                null,
                candidate.Value.Confidence,
                false)) ?? string.Empty;
            if (candidate.Value.HeadingLevel is { } level)
            {
                NewHeadingLevel = level;
            }

            Status = "OCR 预填完成；确认或修改内容后插入草稿。";
            return Result.Success();
        });
    }

    private async Task SplitSelectedAsync()
    {
        if (EditSessionId is null || SelectedBox is null || SelectedBox.IsLogicalPage)
        {
            Status = "请选择一个叶子边界框后再拆分。";
            return;
        }

        DocumentBox? original = _loadedBoxes.FirstOrDefault(box => box.BoxId == SelectedBox.BoxId);
        if (original is null)
        {
            Status = "选中的边界框已不在当前草稿中。";
            return;
        }

        string text = SelectedBox.Text ?? string.Empty;
        string[] parts = text.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.None);
        SplitSource = original;
        SplitFirstText = parts.Length > 1 ? parts[0] : text[..(text.Length / 2)];
        SplitSecondText = parts.Length > 1 ? string.Join("\n\n", parts.Skip(1)) : text[(text.Length / 2)..];
        SetActiveTool(PdfWorkspaceTool.CreateBox);
        Status = SplitStepText;
        await Task.CompletedTask;
    }

    private async Task ConfirmSplitAsync()
    {
        if (EditSessionId is null || SplitSource is null || SplitFirstBBox is null || SplitSecondBBox is null ||
            string.IsNullOrWhiteSpace(SplitFirstText) || string.IsNullOrWhiteSpace(SplitSecondText))
        {
            Status = "必须先框出两个替代区域，并分别填写非空内容。";
            return;
        }

        Result<IReadOnlyList<DocumentBox>> result =
            await (await _main.ServicesAsync()).DocumentTreeEditor.SplitLeafAsync(
                EditSessionId.Value,
                new SplitLeafCommand(SplitSource.BoxId, SplitFirstBBox.Value,
                    CreatePayload(SplitSource.BoxType, SplitFirstText, SplitSource.Payload), SplitSecondBBox.Value,
                    CreatePayload(SplitSource.BoxType, SplitSecondText, SplitSource.Payload)));
        if (result.IsFailure)
        {
            Status = $"拆分边界框失败：{result.ErrorMessage}";
            return;
        }

        ClearSplit();
        await RefreshBoxesAsync();
        Status = "边界框已原子拆分为两个带区域的叶子边界框，并写入页面草稿。";
    }

    private async Task RunLocalOcrAsync()
    {
        if (_currentPageId is null || string.IsNullOrWhiteSpace(Item.DocumentInstanceId) || SelectedBox is null)
        {
            Status = "请先选择叶子边界框并加载页面。";
            return;
        }

        DocumentBox? source = _loadedBoxes.FirstOrDefault(box => box.BoxId == SelectedBox.BoxId);
        if (source is null || source.BoxType == DocumentBoxType.LogicalPage)
        {
            Status = "局部 OCR 只能作用于叶子边界框。";
            return;
        }

        OcrPresetId? presetId = await ResolveOcrPresetIdAsync(OcrScope.Region);
        if (presetId is null)
        {
            return;
        }

        await RunOcrModalAsync("局部 OCR", "正在识别所选边界框区域...", async () =>
        {
            Result<OcrRegionCandidate> candidate = await (await _main.ServicesAsync()).Ocr
                .RecognizeRegionCandidateAsync(
                    DocumentInstanceId.Parse(Item.DocumentInstanceId), presetId.Value, _currentPageId.Value,
                    source.BBox);
            if (candidate.IsFailure)
            {
                Status = $"局部 OCR 失败：{candidate.ErrorMessage}";
                return Result.Failure(candidate.ErrorCode!, candidate.ErrorMessage!);
            }

            LocalOcrCandidate = candidate.Value;
            _localOcrTargetBoxId = source.BoxId;
            LocalOcrSourceText = PayloadTextFor(source) ?? string.Empty;
            CandidateBoxes.Clear();
            CandidateBoxes.Add(new PdfBBoxViewModel(_main, this, new DocumentBox(
                _draftRevisionId ?? _currentRevisionId ?? DocumentTreeRevisionId.New(),
                DocumentBoxId.New(),
                DocumentInstanceId.Parse(Item.DocumentInstanceId),
                candidate.Value.PageId,
                null,
                null,
                candidate.Value.BoxType,
                null,
                null,
                candidate.Value.BBox,
                candidate.Value.Payload,
                candidate.Value.HeadingLevel,
                null,
                candidate.Value.Confidence,
                false), WidthPixels, HeightPixels, true));

            Status = "局部 OCR 候选结果已生成；这是一个短生命周期的完整内容差异，不会写入识别记录或工作版本。";
            return Result.Success();
        });
        if (LocalOcrCandidate is not null && _localOcrTargetBoxId is { } targetId &&
            BoundingBoxes.FirstOrDefault(box => box.BoxId == targetId) is { } target)
        {
            await OpenBoxEditorAsync(target);
        }
    }

    private async Task AcceptLocalOcrAsync()
    {
        if (EditSessionId is null || _localOcrTargetBoxId is null || LocalOcrCandidate is null)
        {
            Status = "请先运行局部 OCR 并选择目标叶子边界框。";
            return;
        }

        DocumentBox? target = _loadedBoxes.FirstOrDefault(box => box.BoxId == _localOcrTargetBoxId.Value);
        if (target is null)
        {
            Status = "候选结果与目标边界框无法匹配。";
            return;
        }

        Result result = await (await _main.ServicesAsync()).DocumentTreeEditor.AcceptLocalOcrCandidateAsync(
            EditSessionId.Value, target.BoxId,
            new LocalOcrCandidate(LocalOcrCandidate.BoxType, LocalOcrCandidate.Payload,
                LocalOcrCandidate.HeadingLevel));
        if (result.IsFailure)
        {
            Status = $"接受局部 OCR 候选结果失败：{result.ErrorMessage}";
            return;
        }

        ClearLocalOcrCandidate();
        await RefreshBoxesAsync();
        Status = "局部 OCR 候选结果已接受并写入页面草稿。";
    }

    private void ClearLocalOcrCandidate()
    {
        CandidateBoxes.Clear();
        LocalOcrCandidate = null;
        _localOcrTargetBoxId = null;
        LocalOcrSourceText = string.Empty;
    }

    private async Task RunLogicalPageOcrAsync()
    {
        if (_currentPageId is null || string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
        {
            Status = "请先加载页面。";
            return;
        }

        DocumentBox[] logicalPages = _loadedBoxes.Where(box => box.BoxType == DocumentBoxType.LogicalPage).ToArray();
        if (logicalPages.Length == 0)
        {
            Status = "当前物理页没有逻辑页；请使用整页 OCR。";
            return;
        }

        OcrPresetId? presetId = await ResolveOcrPresetIdAsync(OcrScope.Page);
        if (presetId is null)
        {
            return;
        }

        await RunOcrModalAsync("逻辑页 OCR", "正在按逻辑页识别本页...", async () =>
        {
            HostServices services = await _main.ServicesAsync();
            Result<LogicalPageOcrResult> result = await services.LogicalPageOcr.RunAsync(
                DocumentInstanceId.Parse(Item.DocumentInstanceId), presetId.Value,
                _currentPageId.Value, OrderSiblings(logicalPages)
                    .Select(box => new LogicalPageOcrTarget(box.BoxId, box.BBox)).ToArray());
            if (result.IsFailure)
            {
                Status = $"逻辑页 OCR 失败：{result.ErrorMessage}";
                return Result.Failure(result.ErrorCode!, result.ErrorMessage!);
            }

            Status = "逻辑页 OCR 已完成并生成工作版本；区域已映射回物理页。";
            return Result.Success();
        });
    }

    private async Task RunDocumentOcrAsync()
    {
        if (string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
        {
            Status = "该题录没有可识别的文档实例。";
            return;
        }

        OcrPresetId? presetId = await ResolveOcrPresetIdAsync(OcrScope.Document);
        if (presetId is null)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        DocumentInstanceId documentId = DocumentInstanceId.Parse(Item.DocumentInstanceId);
        Result<IReadOnlyList<Page>> pages = await services.Pages.ListPagesAsync(documentId);
        if (pages.IsFailure)
        {
            Status = $"读取物理页失败：{pages.ErrorMessage}";
            return;
        }

        PageId[] pageIds = pages.Value.Select(page => page.PageId).ToArray();
        if (pageIds.Length == 0)
        {
            Status = "该文档没有可识别的物理页。";
            return;
        }

        Result<OcrPresetVersion> version = await services.OcrPresets.GetCurrentVersionAsync(presetId.Value);
        if (version.IsFailure)
        {
            Status = $"读取 OCR preset 版本失败：{version.ErrorMessage}";
            return;
        }

        Result<IOcrQueueScheduler> queue = await services.GetOcrQueueAsync();
        if (queue.IsFailure)
        {
            Status = $"OCR 队列不可用：{queue.ErrorMessage}";
            return;
        }

        _main.OcrQueue.ObserveQueue(queue.Value);
        string adapterKind = version.Value.EngineId == OcrEngineIds.MinerU
            ? OcrAdapterKind.CloudApi
            : OcrAdapterKind.LocalLibrary;
        string? providerId = version.Value.EngineId == OcrEngineIds.MinerU ? ProviderIds.MinerU : null;
        Result<OcrQueueTask> queued = await services.Ocr.QueueDocumentOcrAsync(
            documentId, presetId.Value, pageIds, version.Value.EngineId, adapterKind, providerId,
            OcrQueuePriority.UserStartedDocument);
        if (queued.IsFailure)
        {
            Status = $"文档级 OCR 入队失败：{queued.ErrorMessage}";
            return;
        }

        Status = $"文档级 OCR 已加入后台队列：{queued.Value.TaskId}";
        Item.OcrStatus = Status;
        _main.Report(Status);
        await _main.OcrQueue.RefreshAsync();
    }

    private async Task RunCurrentPageOcrAsync()
    {
        if (_currentPageId is null || string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
        {
            Status = "请先加载页面。";
            return;
        }

        OcrPresetId? presetId = await ResolveOcrPresetIdAsync(OcrScope.Page);
        if (presetId is null)
        {
            return;
        }

        await RunOcrModalAsync("本页 OCR", "正在识别当前物理页...", async () =>
        {
            HostServices services = await _main.ServicesAsync();
            DocumentInstanceId documentId = DocumentInstanceId.Parse(Item.DocumentInstanceId);
            Result<LogicalDocumentOcrPagePlan> plan = await CreatePageOcrPlanAsync(
                services, documentId, _currentPageId.Value);
            if (plan.IsFailure)
            {
                Status = $"读取页面 OCR 计划失败：{plan.ErrorMessage}";
                return Result.Failure(plan.ErrorCode!, plan.ErrorMessage!);
            }

            Result<PhysicalPageOcrResult> result = await services.LogicalPageOcr.RunPageAsync(
                documentId, presetId.Value, plan.Value);
            if (result.IsFailure)
            {
                Status = $"本页 OCR 失败：{result.ErrorMessage}";
                return Result.Failure(result.ErrorCode!, result.ErrorMessage!);
            }

            Status = result.Value.UsedLogicalPages
                ? $"本页 OCR 已按 {result.Value.RunIds.Count} 个逻辑页完成并生成工作版本。"
                : "本页整页 OCR 已完成并生成工作版本。";
            return Result.Success();
        });
    }

    private static async Task<Result<LogicalDocumentOcrPagePlan>> CreatePageOcrPlanAsync(
        HostServices services,
        DocumentInstanceId documentId,
        PageId pageId)
    {
        Result<DocumentTreeRevision>
            revision = await services.DocumentTrees.GetCurrentRevisionAsync(documentId, pageId);
        if (revision.IsFailure)
        {
            return Result<LogicalDocumentOcrPagePlan>.Failure(revision.ErrorCode!, revision.ErrorMessage!);
        }

        Result<IReadOnlyList<DocumentBox>> boxes =
            await services.DocumentTrees.ListBoxesAsync(revision.Value.TreeRevisionId);
        if (boxes.IsFailure)
        {
            return Result<LogicalDocumentOcrPagePlan>.Failure(boxes.ErrorCode!, boxes.ErrorMessage!);
        }

        DocumentBox[] logicalPages = boxes.Value.Where(box => box.BoxType == DocumentBoxType.LogicalPage).ToArray();
        return Result<LogicalDocumentOcrPagePlan>.Success(new LogicalDocumentOcrPagePlan(pageId,
            OrderSiblings(logicalPages).Select(box => new LogicalPageOcrTarget(box.BoxId, box.BBox)).ToArray()));
    }

    private void ClearSplit()
    {
        SplitSource = null;
        SplitFirstBBox = null;
        SplitSecondBBox = null;
        SplitDraftBoxes.Clear();
        SplitFirstText = string.Empty;
        SplitSecondText = string.Empty;
    }

    private async Task MergeSelectedAsync()
    {
        if (EditSessionId is null)
        {
            Status = "请先进入编辑模式，再合并边界框。";
            return;
        }

        if (SelectedBoxes.Count < 2)
        {
            Status = "请框选或按住 Ctrl 点选至少两个相邻的叶子边界框后再合并。";
            return;
        }

        DocumentBox[] selected = SelectedBoxes
            .Select(view => _loadedBoxes.FirstOrDefault(box => box.BoxId == view.BoxId))
            .Where(box => box is not null)
            .Cast<DocumentBox>()
            .ToArray();
        DocumentBoxId? parent = selected.Length > 0 ? selected[0].ParentBoxId : null;
        bool valid = selected.Length == SelectedBoxes.Count &&
                     selected.All(box => box.ParentBoxId == parent) &&
                     selected.All(box => !_loadedBoxes.Any(child => child.ParentBoxId == box.BoxId));
        DocumentBox[] mergeBoxes = [];
        if (valid)
        {
            List<DocumentBox> ordered = OrderSiblings(_loadedBoxes.Where(box => box.ParentBoxId == parent)).ToList();
            HashSet<DocumentBoxId> selectedIds = selected.Select(box => box.BoxId).ToHashSet();
            mergeBoxes = ordered.Where(box => selectedIds.Contains(box.BoxId)).ToArray();
            int start = ordered.FindIndex(box => box.BoxId == mergeBoxes[0].BoxId);
            bool contiguous = start >= 0 && ordered.Skip(start).Take(mergeBoxes.Length)
                .Select(box => box.BoxId).SequenceEqual(mergeBoxes.Select(box => box.BoxId));
            valid = mergeBoxes.Length >= 2 && contiguous &&
                    mergeBoxes.All(box =>
                        box.BoxType == mergeBoxes[0].BoxType && box.HeadingLevel == mergeBoxes[0].HeadingLevel);
        }

        if (!valid)
        {
            Status = "合并需要同一父级下、阅读顺序连续且类型相同的叶子边界框。";
            return;
        }

        PendingMergeBoxes = mergeBoxes;
        MergeText = string.Join("\n\n", mergeBoxes.Select(PayloadTextFor)
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        Status = "请检查并编辑合并结果内容，然后显式确认。";
        await Task.CompletedTask;
    }

    private async Task ConfirmMergeAsync()
    {
        if (EditSessionId is null || PendingMergeBoxes.Length < 2 || string.IsNullOrWhiteSpace(MergeText))
        {
            Status = "合并结果必须提供非空内容。";
            return;
        }

        DocumentBox selected = PendingMergeBoxes[0];
        Result<DocumentBox> result = await (await _main.ServicesAsync()).DocumentTreeEditor.MergeLeavesAsync(
            EditSessionId.Value,
            new MergeLeavesCommand(PendingMergeBoxes.Select(box => box.BoxId).ToArray(), CreatePayload(
                selected.BoxType, MergeText,
                selected.Payload)));
        if (result.IsFailure)
        {
            Status = $"合并边界框失败：{result.ErrorMessage}";
            return;
        }

        ClearMerge();
        await RefreshBoxesAsync();
        Status = "相邻边界框已合并到页面草稿。";
    }

    private void ClearMerge()
    {
        PendingMergeBoxes = [];
        MergeText = string.Empty;
    }

    private async Task DeleteSelectedAsync()
    {
        if (!IsEditMode || EditSessionId is not { } sessionId)
        {
            return;
        }

        HashSet<DocumentBoxId> selectedIds = SelectedBoxes.Select(box => box.BoxId).ToHashSet();
        PdfBBoxViewModel[] targets = SelectedBoxes
            .Where(box => box.ParentBoxId is not { } parent || !selectedIds.Contains(parent)).ToArray();
        if (targets.Length == 0)
        {
            Status = "请先选择要删除的边界框。";
            return;
        }

        IDocumentTreeEditor editor = (await _main.ServicesAsync()).DocumentTreeEditor;
        foreach (PdfBBoxViewModel target in targets)
        {
            Result result = await editor.DeleteBoxAsync(sessionId, target.BoxId);
            if (result.IsFailure)
            {
                await RefreshBoxesAsync();
                Status = $"删除边界框失败：{result.ErrorMessage}";
                return;
            }
        }

        await RefreshBoxesAsync();
        Status = "选中边界框及其子级已从页面草稿删除；提交后才会生成新版本。";
    }

    private async Task ToggleSelectedSuppressedAsync()
    {
        PdfBBoxViewModel[] targets = SelectedBoxes.ToArray();
        if (targets.Length == 0)
        {
            Status = "请先选择要排除/纳入的边界框。";
            return;
        }

        foreach (PdfBBoxViewModel target in targets)
        {
            await target.ToggleSuppressedAsync();
        }

        if (targets.Length > 1)
        {
            Status = $"已切换 {targets.Length} 个边界框的文档流状态。";
        }
    }

    private async Task MoveSelectedAsync(bool down)
    {
        if (EditSessionId is null || SelectedBoxes.Count == 0)
        {
            Status = "请先选择要移动的边界框。";
            return;
        }

        PageEditSessionId sessionId = EditSessionId.Value;
        HashSet<DocumentBoxId> selectedIds = SelectedBoxes.Select(box => box.BoxId).ToHashSet();
        List<DocumentBoxId?> parents = _loadedBoxes
            .Where(box => selectedIds.Contains(box.BoxId))
            .Select(box => box.ParentBoxId)
            .Distinct()
            .ToList();
        if (parents.Count == 0)
        {
            return;
        }

        bool moved = false;
        foreach (DocumentBoxId? parent in parents)
        {
            moved |= await MoveSiblingGroupAsync(sessionId, parent, selectedIds, down);
        }

        if (!moved)
        {
            Status = "边界框已在该父级的边界位置。";
            return;
        }

        await RefreshBoxesAsync();
        Status = "边界框顺序已写入页面草稿。";
    }

    // Moves every selected sibling within one parent one step up/down as a block:
    // each contiguous run of selected boxes swaps with the unselected box next to it.
    private async Task<bool> MoveSiblingGroupAsync(
        PageEditSessionId sessionId,
        DocumentBoxId? parentId,
        HashSet<DocumentBoxId> selectedIds,
        bool down)
    {
        List<DocumentBox> ordered = OrderSiblings(_loadedBoxes.Where(box => box.ParentBoxId == parentId)).ToList();
        List<DocumentBox> desired = [.. ordered];
        for (int i = 0; i < desired.Count;)
        {
            if (!selectedIds.Contains(desired[i].BoxId))
            {
                i++;
                continue;
            }

            int runStart = i;
            while (i < desired.Count && selectedIds.Contains(desired[i].BoxId))
            {
                i++;
            }

            int runEnd = i;
            int swapIndex = down ? runEnd : runStart - 1;
            if (swapIndex < 0 || swapIndex >= desired.Count)
            {
                continue;
            }

            DocumentBox other = desired[swapIndex];
            desired.RemoveAt(swapIndex);
            desired.Insert(down ? runStart : runEnd - 1, other);
            if (down)
            {
                i = runEnd + 1;
            }
        }

        // Apply only the moves needed to reach the desired order, top to bottom,
        // so each box is anchored after a predecessor that is already in place.
        bool moved = false;
        List<DocumentBox> current = [.. ordered];
        for (int position = 0; position < desired.Count; position++)
        {
            DocumentBox box = desired[position];
            DocumentBoxId? after = position > 0 ? desired[position - 1].BoxId : null;
            int currentIndex = current.FindIndex(candidate => candidate.BoxId == box.BoxId);
            DocumentBoxId? currentPredecessor = currentIndex > 0 ? current[currentIndex - 1].BoxId : null;
            if (Equals(currentPredecessor, after))
            {
                continue;
            }

            Result result = await (await _main.ServicesAsync()).DocumentTreeEditor.MoveBoxAsync(
                sessionId, new MoveBoxCommand(box.BoxId, parentId, after));
            if (result.IsFailure)
            {
                Status = $"移动边界框失败：{result.ErrorMessage}";
                return moved;
            }

            current.RemoveAt(currentIndex);
            current.Insert(after is null ? 0 : current.FindIndex(candidate => candidate.BoxId == after) + 1, box);
            moved = true;
        }

        return moved;
    }

    internal async Task MoveBoxToAsync(PdfBBoxViewModel movingView, PdfBBoxViewModel targetView, bool insertBefore)
    {
        if (EditSessionId is null || movingView.BoxId == targetView.BoxId)
        {
            return;
        }

        // Dragging a box that belongs to a multi-selection moves the whole selection,
        // keeping the selection's reading order.
        List<PdfBBoxViewModel> movingViews = SelectedBoxes.Count > 1 && SelectedBoxes.Contains(movingView)
            ? SelectedBoxes.OrderBy(view => BoundingBoxes.IndexOf(view)).ToList()
            : [movingView];
        if (movingViews.Any(view => view.BoxId == targetView.BoxId))
        {
            return;
        }

        DocumentBox? target = _loadedBoxes.FirstOrDefault(box => box.BoxId == targetView.BoxId);
        if (target is null)
        {
            return;
        }

        DocumentBoxId? parent;
        DocumentBoxId? after;
        if (!insertBefore && target.BoxType == DocumentBoxType.LogicalPage)
        {
            parent = target.BoxId;
            after = LastSiblingForParent(target.BoxId)?.BoxId;
        }
        else
        {
            parent = target.ParentBoxId;
            if (insertBefore)
            {
                List<DocumentBox> siblings = OrderSiblings(
                    _loadedBoxes.Where(box => box.ParentBoxId == target.ParentBoxId)).ToList();
                int targetIndex = siblings.FindIndex(box => box.BoxId == target.BoxId);
                after = targetIndex > 0 ? siblings[targetIndex - 1].BoxId : null;
            }
            else
            {
                after = target.BoxId;
            }
        }

        IDocumentTreeEditor editor = (await _main.ServicesAsync()).DocumentTreeEditor;
        foreach (PdfBBoxViewModel view in movingViews)
        {
            DocumentBox? moving = _loadedBoxes.FirstOrDefault(box => box.BoxId == view.BoxId);
            if (moving is null)
            {
                continue;
            }

            Result result = await editor.MoveBoxAsync(
                EditSessionId.Value, new MoveBoxCommand(moving.BoxId, parent, after));
            if (result.IsFailure)
            {
                Status = $"拖放移动失败：{result.ErrorMessage}";
                return;
            }

            after = moving.BoxId;
        }

        await RefreshBoxesAsync();
        Status = movingViews.Count > 1
            ? "选中边界框已通过拖放移动到页面草稿。"
            : "边界框已通过拖放移动到页面草稿。";
    }

    internal async Task OpenBoxEditorAsync(PdfBBoxViewModel box)
    {
        if (box.IsLogicalPage)
        {
            return;
        }

        await _main.Dialogs.ShowDialogAsync(box);
    }

    private List<DocumentBox> OrderedSelectedLoadedBoxes()
    {
        HashSet<DocumentBoxId> selectedIds = SelectedBoxes.Select(box => box.BoxId).ToHashSet();
        return OrderedTree(_loadedBoxes)
            .Select(item => item.Box)
            .Where(box => selectedIds.Contains(box.BoxId))
            .ToList();
    }

    // Level changes require a single-level selection: multi-select either reorders within one
    // parent (move up/down, drag) or changes level as a whole group, never both at once.
    private async Task IndentSelectedAsync()
    {
        if (EditSessionId is null || SelectedBoxes.Count == 0)
        {
            Status = "请先选择要移入的边界框。";
            return;
        }

        List<DocumentBox> selected = OrderedSelectedLoadedBoxes();
        if (selected.Count == 0)
        {
            return;
        }

        DocumentBoxId? parent = selected[0].ParentBoxId;
        if (selected.Any(box => box.ParentBoxId != parent))
        {
            Status = "多选移入/移出要求选区在同一父级下。";
            return;
        }

        if (selected.Any(box => box.BoxType == DocumentBoxType.LogicalPage))
        {
            Status = "逻辑页不能移入其他边界框。";
            return;
        }

        HashSet<DocumentBoxId> selectedIds = selected.Select(box => box.BoxId).ToHashSet();
        List<DocumentBox> siblings = OrderSiblings(_loadedBoxes.Where(box => box.ParentBoxId == parent)).ToList();
        int firstIndex = siblings.FindIndex(box => selectedIds.Contains(box.BoxId));
        DocumentBox? newParent = null;
        for (int i = firstIndex - 1; i >= 0; i--)
        {
            if (!selectedIds.Contains(siblings[i].BoxId))
            {
                newParent = siblings[i];
                break;
            }
        }

        if (newParent is null)
        {
            Status = "上方没有可作为父级的兄弟边界框。";
            return;
        }

        IDocumentTreeEditor editor = (await _main.ServicesAsync()).DocumentTreeEditor;
        DocumentBoxId? after = LastSiblingForParent(newParent.BoxId)?.BoxId;
        foreach (DocumentBox box in selected)
        {
            Result result = await editor.MoveBoxAsync(
                EditSessionId.Value, new MoveBoxCommand(box.BoxId, newParent.BoxId, after));
            if (result.IsFailure)
            {
                Status = $"移入边界框失败：{result.ErrorMessage}";
                return;
            }

            after = box.BoxId;
        }

        await RefreshBoxesAsync();
        Status = "选中边界框已移入上方兄弟框。";
    }

    private async Task OutdentSelectedAsync()
    {
        if (EditSessionId is null || SelectedBoxes.Count == 0)
        {
            Status = "请先选择要移出的边界框。";
            return;
        }

        List<DocumentBox> selected = OrderedSelectedLoadedBoxes();
        if (selected.Count == 0)
        {
            return;
        }

        DocumentBoxId? parent = selected[0].ParentBoxId;
        if (selected.Any(box => box.ParentBoxId != parent))
        {
            Status = "多选移入/移出要求选区在同一父级下。";
            return;
        }

        if (parent is null)
        {
            Status = "选中的边界框已在顶层，无法移出。";
            return;
        }

        DocumentBox? parentBox = _loadedBoxes.FirstOrDefault(box => box.BoxId == parent.Value);
        if (parentBox is null)
        {
            return;
        }

        IDocumentTreeEditor editor = (await _main.ServicesAsync()).DocumentTreeEditor;
        DocumentBoxId? after = parent.Value;
        foreach (DocumentBox box in selected)
        {
            Result result = await editor.MoveBoxAsync(
                EditSessionId.Value, new MoveBoxCommand(box.BoxId, parentBox.ParentBoxId, after));
            if (result.IsFailure)
            {
                Status = $"移出边界框失败：{result.ErrorMessage}";
                return;
            }

            after = box.BoxId;
        }

        await RefreshBoxesAsync();
        Status = "选中边界框已移出到上一级。";
    }

    private async Task OpenNewBoxEditorAsync()
    {
        await _main.Dialogs.ShowDialogAsync(this);
    }

    private static IEnumerable<DocumentBox> OrderSiblings(IEnumerable<DocumentBox> boxes)
    {
        DocumentBox[] values = boxes.ToArray();
        HashSet<DocumentBoxId> ids = values.Select(box => box.BoxId).ToHashSet();
        HashSet<DocumentBoxId> referenced = values.Where(box => box.NextSiblingBoxId is not null)
            .Select(box => box.NextSiblingBoxId!.Value).ToHashSet();
        DocumentBox? current = values.FirstOrDefault(box => !referenced.Contains(box.BoxId));
        HashSet<DocumentBoxId> visited = [];
        while (current is not null && visited.Add(current.BoxId))
        {
            yield return current;
            current = current.NextSiblingBoxId is { } next && ids.Contains(next)
                ? values.FirstOrDefault(box => box.BoxId == next)
                : null;
        }
    }

    private string? PayloadTextFor(DocumentBox box)
    {
        return box.Payload switch
        {
            TextBoxPayload value => value.Markdown,
            ListBoxPayload value => value.Markdown,
            TableBoxPayload value => value.Markdown,
            EquationBoxPayload value => value.Latex,
            CodeBoxPayload value => value.Code,
            MediaBoxPayload value => value.Description,
            _ => null
        };
    }

    private void ClearPendingBox()
    {
        PendingBBox = null;
        NewBoxText = string.Empty;
        SelectionWidth = 0;
        SelectionHeight = 0;
    }

    public void RemoveBBox(PdfBBoxViewModel bbox)
    {
        BoundingBoxes.Remove(bbox);
        SelectedBoxes.Remove(bbox);
        bbox.IsSelected = false;
        foreach (MarkdownPreviewBlockViewModel block in PreviewBlocks.Where(block => block.BoxId == bbox.BoxId)
                     .ToArray())
        {
            PreviewBlocks.Remove(block);
        }

        if (_selectedBox == bbox)
        {
            SetPrimaryBox(null);
        }
    }

    public async Task LoadAsync()
    {
        PageIndex = 0;
        _lastNavigationDirection = 0;
        _renderGeneration++;
        await RenderCurrentPageAsync();
    }

    public void Clear()
    {
        _prefetchSubject.OnNext(default);
        ReleaseDocumentSessionAsync().Observe(nameof(PdfWorkspaceViewModel), nameof(ReleaseDocumentSessionAsync));
        ExitBookReading();
        PageEditSessionId? sessionId = EditSessionId;
        Image?.Dispose();
        Image = null;
        PageIndex = 0;
        PageCount = 0;
        WidthPixels = 0;
        HeightPixels = 0;
        _renderGeneration++;
        IsBusy = false;
        IsEditMode = false;
        IsViewingHistoricalRevision = false;
        IsSidebarOpen = false;
        SetActiveTool(PdfWorkspaceTool.Select);
        BoundingBoxes.Clear();
        TreeBoxes.Clear();
        PageRevisions.Clear();
        OverlapMarkers.Clear();
        ContinuationLinks.Clear();
        CrossPageContinuationMarkers.Clear();
        _crossPageContinuationSources.Clear();
        _pages = [];
        PreviewBlocks.Clear();
        ReadingScene = null;
        SelectedBox = null;
        _currentRevisionId = null;
        _draftRevisionId = null;
        EditSessionId = null;
        _loadedBoxes = [];
        _collapsedBoxIds.Clear();
        ClearSplit();
        ClearPendingBox();
        ClearLocalOcrCandidate();
        ClearSourceValidation();
        Status = "选择题录后可预览 PDF。";
        if (sessionId is not null)
        {
            DiscardClearedDraftAsync(sessionId.Value)
                .Observe(nameof(PdfWorkspaceViewModel), nameof(DiscardClearedDraftAsync));
        }
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_prefetchSubject.IsDisposed)
        {
            Clear();
            _prefetchSubject.Dispose();
            _bookReadingSubject.Dispose();
            _bookReadingInitialWindowPending = false;
        }

        base.Dispose(disposing);
    }

    private async Task PreviousPageAsync()
    {
        if (IsEditMode)
        {
            Status = "请先提交或放弃当前页面草稿，再切换页面。";
            return;
        }

        if (PageIndex <= 0)
        {
            return;
        }

        PageIndex--;
        _lastNavigationDirection = -1;
        await RenderCurrentPageAsync();
    }

    private async Task NextPageAsync()
    {
        if (IsEditMode)
        {
            Status = "请先提交或放弃当前页面草稿，再切换页面。";
            return;
        }

        if (PageCount > 0 && PageIndex >= PageCount - 1)
        {
            return;
        }

        PageIndex++;
        _lastNavigationDirection = 1;
        await RenderCurrentPageAsync();
    }

    internal async Task GoToPageAsync(int pageNumber)
    {
        if (IsEditMode)
        {
            Status = "请先提交或放弃当前页面草稿，再切换页面。";
            Raise(nameof(PageNumberText));
            return;
        }

        if (PageCount <= 0)
        {
            return;
        }

        int target = Math.Clamp(pageNumber - 1, 0, PageCount - 1);
        if (target == PageIndex)
        {
            Raise(nameof(PageNumberText));
            return;
        }

        _lastNavigationDirection = target > PageIndex ? 1 : target < PageIndex ? -1 : 0;
        PageIndex = target;
        await RenderCurrentPageAsync();
    }

    internal bool TryHighlightBox(DocumentBoxId boxId)
    {
        PdfBBoxViewModel? box = BoundingBoxes.FirstOrDefault(candidate => candidate.BoxId == boxId);
        if (box is null)
        {
            return false;
        }

        SelectedBox = box;
        IsSidebarOpen = true;
        return true;
    }

    private Task ReloadAsync()
    {
        return RenderCurrentPageAsync();
    }

    public void AdjustZoom(double delta)
    {
        SetZoom(Zoom + delta);
    }

    private void SetZoom(double value)
    {
        Zoom = Math.Clamp(value, 0.25, 4.0);
    }

    private async Task RenderCurrentPageAsync()
    {
        int generation = ++_renderGeneration;
        Image?.Dispose();
        Image = null;
        WidthPixels = 0;
        IsBusy = true;
        BeginSourceValidation();
        Status = "正在渲染 PDF 预览...";

        try
        {
            if (string.IsNullOrWhiteSpace(Item.DocumentInstanceId) || string.IsNullOrWhiteSpace(Item.FileAssetId))
            {
                if (string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
                {
                    ClearSourceValidation();
                    Status = "该题录没有可预览的 PDF 文件。";
                    return;
                }
            }

            HostServices services = await _main.ServicesAsync();
            DocumentInstanceId documentInstanceId = DocumentInstanceId.Parse(Item.DocumentInstanceId);
            FileAssetId? fileAssetId = await ResolveFileAssetIdAsync(services, documentInstanceId);
            if (fileAssetId is null)
            {
                ClearSourceValidation();
                Status = "该题录没有可预览的 PDF 文件。";
                return;
            }

            Result<IReadOnlyList<Page>> pages = await services.Pages.ListPagesAsync(documentInstanceId);
            if (pages.IsFailure)
            {
                ClearSourceValidation();
                Status = $"ERROR {pages.ErrorCode}: {pages.ErrorMessage}";
                return;
            }

            PageCount = pages.Value.Count;
            _pages = pages.Value;
            if (PageCount == 0)
            {
                ClearSourceValidation();
                Status = "该文档还没有页面记录。";
                return;
            }

            PageIndex = Math.Clamp(PageIndex, 0, PageCount - 1);
            Page page = pages.Value[PageIndex];
            _currentPageId = page.PageId;
            PageRenderRequest renderRequest = new(documentInstanceId, page.PageId, fileAssetId, 120,
                Purpose: PageRenderPurpose.Preview);
            Result<PdfPagePixelBufferLease> preview = PageRenderPreviewHandler is not null
                ? await PageRenderPreviewHandler(renderRequest, CancellationToken.None)
                : await services.PageRenders.RenderPreviewAsync(renderRequest);
            if (preview.IsFailure)
            {
                ApplyPreviewFailure(preview);
                Status = $"ERROR {preview.ErrorCode}: {preview.ErrorMessage}";
                return;
            }

            if (generation != _renderGeneration)
            {
                preview.Value.Dispose();
                return;
            }

            using PdfPagePixelBufferLease raster = preview.Value;
            Image = CreateBitmap(raster);
            WidthPixels = raster.WidthPixels;
            HeightPixels = raster.HeightPixels;

            BoundingBoxes.Clear();
            PreviewBlocks.Clear();
            ReadingScene = null;
            SelectedBox = null;
            if (IsEditMode && _draftRevisionId != null)
            {
                await LoadBoxesIntoViewAsync(_draftRevisionId.Value, true);
            }
            else
            {
                Result<DocumentTreeRevision> rev =
                    await services.DocumentTrees.GetCurrentRevisionAsync(documentInstanceId, page.PageId);
                if (rev.IsSuccess)
                {
                    _currentRevisionId = rev.Value.TreeRevisionId;
                    _liveCurrentRevisionId = rev.Value.TreeRevisionId;
                    IsViewingHistoricalRevision = false;
                    await LoadBoxesIntoViewAsync(rev.Value.TreeRevisionId, false);
                }
            }

            await LoadPageRevisionsAsync(services, documentInstanceId, page.PageId);

            Status =
                $"{Item.Title} · 第 {PageIndex + 1}/{PageCount} 页 · {raster.WidthPixels}x{raster.HeightPixels} · {raster.RendererBasisVersion}";
            CompleteSourceValidation(SourceValidationStatus.Current, null);
            SchedulePrefetchAsync(services, documentInstanceId, fileAssetId);
        }
        catch (Exception ex)
        {
            ClearSourceValidation();
            Status = $"PDF 预览失败：{ex.Message}";
        }
        finally
        {
            if (generation == _renderGeneration)
            {
                IsBusy = false;
            }
        }
    }

    // AC16: the workspace exposes a lazy source-validation state machine. It stays unverified
    // until a render that may validate runs, is observed as "validating" only while that render
    // is in flight (UI stays interactive), and settles on a terminal state with a distinct
    // warning for changed/unavailable sources. Non-source outcomes leave it unverified.
    private void BeginSourceValidation()
    {
        SourceWarning = null;
        SourceValidationState = SourceValidationStatus.Validating;
    }

    private void CompleteSourceValidation(string terminalState, string? warning)
    {
        SourceWarning = warning;
        SourceValidationState = terminalState;
    }

    private void ClearSourceValidation()
    {
        CompleteSourceValidation(SourceValidationStatus.Unverified, null);
    }

    private void ApplyPreviewFailure(Result<PdfPagePixelBufferLease> preview)
    {
        string message = preview.ErrorMessage ?? string.Empty;
        if (message.Contains("bbox_basis_stale", StringComparison.Ordinal) ||
            message.Contains("source_changed", StringComparison.Ordinal))
        {
            CompleteSourceValidation(SourceValidationStatus.Changed, message);
            return;
        }

        if (preview.ErrorCode == AppErrorCodes.NotFound ||
            message.Contains("unavailable", StringComparison.OrdinalIgnoreCase) ||
            message.Contains("source file", StringComparison.OrdinalIgnoreCase))
        {
            CompleteSourceValidation(SourceValidationStatus.Unavailable, message);
            return;
        }

        ClearSourceValidation();
    }

    // Adjacent-page pre-fetch. Current page rendering keeps the highest priority; prefetch
    // only warms the shared preview raster cache (bounded LRU), never mutates the current
    // page, and failures are swallowed so a prefetch can never change the current page's
    // success state. Fast navigation cancels the previous prefetch window.
    private void SchedulePrefetchAsync(HostServices services, DocumentInstanceId documentInstanceId,
        FileAssetId? fileAssetId)
    {
        // Keep the window ordered and render it serially. Starting all neighbours at once can
        // put low-priority PDFium work ahead of the page the user has just requested. Identical
        // foreground/prefetch requests still merge in PageRenderService's in-flight cache.
        int[] targets = PrefetchWindow(PageIndex, PageCount, _lastNavigationDirection);
        _prefetchSubject.OnNext(new PrefetchRequest(services, documentInstanceId, fileAssetId, targets));
    }

    private async Task PrefetchWindowAsync(HostServices services, DocumentInstanceId documentInstanceId,
        FileAssetId? fileAssetId, IReadOnlyList<int> pageIndexes, CancellationToken cancellationToken)
    {
        try
        {
            foreach (int pageIndex in pageIndexes)
            {
                cancellationToken.ThrowIfCancellationRequested();
                await PrefetchPageAsync(services, documentInstanceId, fileAssetId, pageIndex, cancellationToken);
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // A newer page selection owns the next prefetch window.
        }
        catch (Exception ex)
        {
            UnexpectedExceptions.Sink.Report(ex, nameof(PdfWorkspaceViewModel), nameof(PrefetchWindowAsync));
        }
    }

    private async Task PrefetchPageAsync(HostServices services, DocumentInstanceId documentInstanceId,
        FileAssetId? fileAssetId, int pageIndex, CancellationToken cancellationToken)
    {
        try
        {
            if (pageIndex < 0 || pageIndex >= _pages.Count || pageIndex == PageIndex)
            {
                return;
            }

            Page page = _pages[pageIndex];
            Result<PdfPagePixelBufferLease> preview = await services.PageRenders.RenderPreviewAsync(
                new PageRenderRequest(documentInstanceId, page.PageId, fileAssetId, 120,
                    Purpose: PageRenderPurpose.Preview), cancellationToken);
            if (preview.IsSuccess)
            {
                preview.Value.Dispose();
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception)
        {
            // Pre-fetch must never affect the current page's success state.
        }
    }

    // Default working set is current, previous, and the next two pages. While navigating
    // forward/back the window is ordered by direction so the pages closest to the user's
    // next move are prefetched first.
    private static int[] PrefetchWindow(int current, int pageCount, int direction)
    {
        int[] window = direction > 0
            ? [current + 1, current + 2, current - 1]
            : direction < 0
                ? [current - 1, current - 2, current + 1]
                : [current - 1, current + 1, current + 2];
        return window.Where(index => index >= 0 && index < pageCount && index != current)
            .Distinct()
            .ToArray();
    }

    private async Task<FileAssetId?> ResolveFileAssetIdAsync(HostServices services,
        DocumentInstanceId documentInstanceId)
    {
        if (!string.IsNullOrWhiteSpace(Item.FileAssetId))
        {
            return FileAssetId.Parse(Item.FileAssetId);
        }

        Result<FileAssetId> result = await services.Pages.GetFileAssetIdAsync(documentInstanceId);
        return result.IsSuccess ? result.Value : null;
    }

    private static WriteableBitmap CreateBitmap(PdfPagePixelBufferLease raster)
    {
        return new WriteableBitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, raster.PixelAddress,
            new PixelSize(raster.WidthPixels, raster.HeightPixels), new Vector(96, 96), raster.Stride);
    }

    private async Task DiscardClearedDraftAsync(PageEditSessionId sessionId)
    {
        try
        {
            Result discarded = await (await _main.ServicesAsync()).DocumentTrees.DiscardPageEditAsync(sessionId);
            if (discarded.IsFailure)
            {
                _main.ReportError($"放弃页面草稿失败：{discarded.ErrorMessage}");
            }
        }
        catch (Exception ex)
        {
            _main.ReportError($"放弃页面草稿失败：{ex.Message}");
        }
    }

    private async Task ReleaseDocumentSessionAsync()
    {
        try
        {
            if (string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
            {
                return;
            }

            await (await _main.ServicesAsync()).PageRenders.ReleaseDocumentSessionAsync(
                DocumentInstanceId.Parse(Item.DocumentInstanceId));
        }
        catch (Exception ex)
        {
            _main.ReportError($"释放 PDF 会话失败：{ex.Message}");
        }
    }

    private DocumentBox? LastSiblingForParent(DocumentBoxId parentBoxId)
    {
        List<DocumentBox> siblings = _loadedBoxes.Where(box => box.ParentBoxId == parentBoxId).ToList();
        if (siblings.Count == 0)
        {
            return null;
        }

        HashSet<DocumentBoxId> siblingIds = siblings.Select(box => box.BoxId).ToHashSet();
        DocumentBox? current = siblings.FirstOrDefault(box => box.NextSiblingBoxId is null ||
                                                              !siblingIds.Contains(box.NextSiblingBoxId.Value));
        while (current?.NextSiblingBoxId is not null && siblingIds.Contains(current.NextSiblingBoxId.Value))
        {
            current = siblings.First(box => box.BoxId == current.NextSiblingBoxId.Value);
        }

        return current;
    }

    private async Task<IReadOnlyList<DocumentBox>> LoadBoxesAsync(DocumentTreeRevisionId revisionId)
    {
        Result<IReadOnlyList<DocumentBox>> result =
            await (await _main.ServicesAsync()).DocumentTrees.ListBoxesAsync(revisionId);
        return result.IsSuccess ? result.Value : [];
    }

    private async Task LoadBoxesIntoViewAsync(DocumentTreeRevisionId revisionId, bool isDraft)
    {
        _loadedBoxes = await LoadBoxesAsync(revisionId);
        int readingOrder = 0;
        foreach ((DocumentBox Box, int Depth) item in OrderedTree(_loadedBoxes))
        {
            DocumentBox box = item.Box;
            BoundingBoxes.Add(new PdfBBoxViewModel(
                _main, this, box, WidthPixels, HeightPixels, isDraft, ++readingOrder, item.Depth));
        }

        foreach (PdfBBoxViewModel view in BoundingBoxes)
        {
            view.HasChildren = _loadedBoxes.Any(box => box.ParentBoxId == view.BoxId);
            view.IsTreeExpanded = !_collapsedBoxIds.Contains(view.BoxId);
        }

        RebuildTreeBoxes();
        await UpdateOverlapWarningsAsync(revisionId);
        await UpdateContinuationLinksAsync();
        await LoadPreviewAsync(revisionId);
    }

    private async Task UpdateContinuationLinksAsync()
    {
        ContinuationLinks.Clear();
        CrossPageContinuationMarkers.Clear();
        _crossPageContinuationSources.Clear();
        Dictionary<DocumentBoxId, PdfBBoxViewModel> byId = BoundingBoxes.ToDictionary(box => box.BoxId);
        List<PdfBBoxViewModel> unresolved = [];
        foreach (PdfBBoxViewModel box in BoundingBoxes)
        {
            box.ContinuationHeadText = null;
            box.ContinuationSourceLabel = null;
            if (box.ContinuesFromBoxId is not { } headId)
            {
                continue;
            }

            if (byId.TryGetValue(headId, out PdfBBoxViewModel? head))
            {
                box.ContinuationHeadText = head.Text;
                ContinuationLinks.Add(new PdfContinuationLinkViewModel(head, box));
            }
            else
            {
                unresolved.Add(box);
            }
        }

        if (unresolved.Count > 0 && !string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
        {
            HostServices services = await _main.ServicesAsync();
            DocumentInstanceId documentInstanceId = DocumentInstanceId.Parse(Item.DocumentInstanceId);
            for (int pageIndex = Math.Min(PageIndex - 1, _pages.Count - 1);
                 pageIndex >= 0 && unresolved.Count > 0;
                 pageIndex--)
            {
                Result<DocumentTreeRevision> revision = await services.DocumentTrees
                    .GetCurrentRevisionAsync(documentInstanceId, _pages[pageIndex].PageId);
                if (revision.IsFailure)
                {
                    continue;
                }

                IReadOnlyList<DocumentBox> candidates = await LoadBoxesAsync(revision.Value.TreeRevisionId);
                foreach (PdfBBoxViewModel box in unresolved.ToArray())
                {
                    DocumentBox? head = candidates.FirstOrDefault(candidate =>
                        candidate.BoxId == box.ContinuesFromBoxId);
                    if (head is null)
                    {
                        continue;
                    }

                    box.ContinuationHeadText = PdfBBoxViewModel.PayloadText(head.Payload);
                    box.ContinuationSourceLabel = $"续接自第 {pageIndex + 1} 页";
                    _crossPageContinuationSources[box.BoxId] = (pageIndex, head.BoxId);
                    CrossPageContinuationMarkers.Add(
                        new PdfCrossPageContinuationViewModel(box, pageIndex + 1));
                    unresolved.Remove(box);
                }
            }
        }

        Raise(nameof(HasContinuationLinks));
        Raise(nameof(HasCrossPageContinuationMarkers));
    }

    internal async Task JumpToContinuationSourceAsync(PdfBBoxViewModel box)
    {
        if (box.ContinuesFromBoxId is not { } headId)
        {
            return;
        }

        PdfBBoxViewModel? local = BoundingBoxes.FirstOrDefault(candidate => candidate.BoxId == headId);
        if (local is not null)
        {
            SelectedBox = local;
            Status = "已选中同页的续接源框。";
            return;
        }

        if (IsEditMode)
        {
            Status = "请先提交或放弃当前页面草稿，再跳转到续接源框。";
            return;
        }

        if (!_crossPageContinuationSources.TryGetValue(
                box.BoxId, out (int PageIndex, DocumentBoxId HeadBoxId) target))
        {
            Status = "未能定位续接源框。";
            return;
        }

        PageIndex = target.PageIndex;
        await RenderCurrentPageAsync();
        SelectedBox = BoundingBoxes.FirstOrDefault(candidate => candidate.BoxId == target.HeadBoxId);
    }

    // AC16: overlap markers are a bounded, revision-keyed lazy projection. The workspace requests
    // the projection for the page it entered; immutable revisions are reused from cache, Box edits
    // invalidate only this page, and the projection never reads the source file or computes a full
    // hash. The in-memory box set is passed through as the provider so there is no second read.
    private async Task UpdateOverlapWarningsAsync(DocumentTreeRevisionId revisionId)
    {
        OverlapMarkers.Clear();
        Dictionary<DocumentBoxId, PdfBBoxViewModel> byId = BoundingBoxes.ToDictionary(box => box.BoxId);
        foreach (PdfBBoxViewModel box in BoundingBoxes)
        {
            box.HasOverlapWarning = false;
        }

        if (_currentPageId is { } pageId)
        {
            IReadOnlyList<DocumentBox> boxes = _loadedBoxes;
            HostServices services = await _main.ServicesAsync();
            Result<IReadOnlyList<DocumentBoxOverlap>> overlaps = await services.Overlaps.GetOrCreateAsync(
                revisionId,
                pageId,
                DocumentBoxOverlapDetector.PolicyBasis,
                _ => Task.FromResult(Result<IReadOnlyList<DocumentBox>>.Success(boxes)));
            if (overlaps.IsSuccess)
            {
                foreach (DocumentBoxOverlap overlap in overlaps.Value)
                {
                    if (byId.TryGetValue(overlap.First.BoxId, out PdfBBoxViewModel? first) &&
                        byId.TryGetValue(overlap.Second.BoxId, out PdfBBoxViewModel? second))
                    {
                        first.HasOverlapWarning = true;
                        second.HasOverlapWarning = true;
                        OverlapMarkers.Add(new PdfOverlapMarkerViewModel(
                            first, second, overlap.Intersection, WidthPixels, HeightPixels));
                    }
                }
            }
        }

        Raise(nameof(HasOverlapWarnings));
    }

    private static IEnumerable<(DocumentBox Box, int Depth)> OrderedTree(IReadOnlyList<DocumentBox> boxes)
    {
        foreach (DocumentBox root in OrderSiblings(boxes.Where(box => box.ParentBoxId is null)))
        {
            foreach ((DocumentBox Box, int Depth) item in OrderedSubtree(boxes, root, 0))
            {
                yield return item;
            }
        }
    }

    private static IEnumerable<(DocumentBox Box, int Depth)> OrderedSubtree(
        IReadOnlyList<DocumentBox> boxes,
        DocumentBox box,
        int depth)
    {
        yield return (box, depth);
        foreach (DocumentBox child in OrderSiblings(boxes.Where(candidate => candidate.ParentBoxId == box.BoxId)))
        {
            foreach ((DocumentBox Box, int Depth) item in OrderedSubtree(boxes, child, depth + 1))
            {
                yield return item;
            }
        }
    }

    internal async Task RefreshPreviewAsync()
    {
        DocumentTreeRevisionId? revisionId = IsEditMode ? _draftRevisionId : _currentRevisionId;
        if (revisionId is not null)
        {
            await LoadPreviewAsync(revisionId.Value);
        }
    }

    internal void RefreshContinuationDependents(DocumentBoxId headBoxId, string? headText)
    {
        foreach (PdfBBoxViewModel box in BoundingBoxes)
        {
            if (box.ContinuesFromBoxId == headBoxId)
            {
                box.ContinuationHeadText = headText;
            }
        }
    }

    internal async Task RefreshBoxesAsync()
    {
        if (_draftRevisionId is null)
        {
            return;
        }

        // AC16: a Box edit invalidates only the affected page's cached overlap projection;
        // the recompute that follows never reads the source file or computes a full hash.
        if (_currentPageId is { } pageId)
        {
            (await _main.ServicesAsync()).Overlaps.Invalidate(pageId);
        }

        BoundingBoxes.Clear();
        SelectedBox = null;
        await LoadBoxesIntoViewAsync(_draftRevisionId.Value, true);
    }

    private async Task LoadPreviewAsync(DocumentTreeRevisionId revisionId)
    {
        PreviewBlocks.Clear();
        ReadingScene = null;
        HostServices services = await _main.ServicesAsync();
        Result<CompiledMarkdown> compiled = await services.DocumentMarkdown.CompilePageMarkdownAsync(
            revisionId, false);
        if (compiled.IsFailure)
        {
            return;
        }

        MarkdownDocumentModel model = compiled.Value.Document ?? services.Markdown.Parse(compiled.Value.Markdown);
        for (int index = 0; index < model.Blocks.Count; index++)
        {
            int previewIndex = index;
            MarkdownBlock block = model.Blocks[index];
            MarkdownSourceMapEntry? source = compiled.Value.SourceMap.FirstOrDefault(entry =>
                previewIndex >= entry.PreviewNodeStart &&
                previewIndex < entry.PreviewNodeStart + entry.PreviewNodeCount);
            DocumentBoxId? boxId = source?.BoxId;
            string kind = _loadedBoxes.FirstOrDefault(box => box.BoxId == boxId)?.BoxType ?? block.Kind;
            PreviewBlocks.Add(new MarkdownPreviewBlockViewModel(
                kind, MarkdownFor(block, compiled.Value.Markdown), block, block.Level, boxId,
                () =>
                {
                    SelectPreviewBox(boxId);
                    return Task.CompletedTask;
                }));

            PreviewBlocks[^1].IsSelected = boxId == _previewSelectedBoxId;
        }

        ReadingScene = DocumentReadingSceneBuilder.Build(model, compiled.Value.SourceMap, _loadedBoxes);
    }

    private async Task CopyMarkdownAsync()
    {
        DocumentTreeRevisionId? revisionId = IsEditMode ? _draftRevisionId : _currentRevisionId;
        if (revisionId is null)
        {
            Status = "请先加载可复制 Markdown 的页面。";
            return;
        }

        Result<CompiledMarkdown> compiled =
            await (await _main.ServicesAsync()).DocumentMarkdown.CompilePageMarkdownAsync(
                revisionId.Value, false);
        if (compiled.IsFailure)
        {
            Status = $"复制 Markdown 失败：{compiled.ErrorMessage}";
            return;
        }

        try
        {
            await _main.Clipboard.SetTextAsync(compiled.Value.Markdown);
            Status = "已复制当前页面 Markdown。";
        }
        catch (Exception exception)
        {
            Status = $"复制 Markdown 失败：{exception.Message}";
        }
    }

    private static string MarkdownFor(MarkdownBlock block, string markdown)
    {
        int start = Math.Clamp(block.Start, 0, markdown.Length);
        int length = Math.Clamp(block.Length, 0, markdown.Length - start);
        return markdown.Substring(start, length);
    }

    private void SelectPreviewBox(DocumentBoxId? boxId)
    {
        if (boxId is not null)
        {
            SelectedBox = BoundingBoxes.FirstOrDefault(box => box.BoxId == boxId.Value);
        }
    }

    // Click in the sidebar reading view: reuse the preview-block selection path so box selection,
    // canvas highlighting and the reading view stay in sync. A null BoxId means "empty space".
    internal void SelectReadingBlock(DocumentBoxId? boxId)
    {
        if (boxId is null)
        {
            ClearSelection();
        }
        else
        {
            SelectPreviewBox(boxId);
        }
    }

    private async Task EnterEditModeAsync()
    {
        if (string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
        {
            Status = "该题录没有可编辑的文档实例。";
            return;
        }

        if (IsViewingHistoricalRevision)
        {
            Status = "当前正在查看历史版本，请重新加载页面后再进入编辑模式。";
            return;
        }

        HostServices services = await _main.ServicesAsync();
        DocumentInstanceId docId = DocumentInstanceId.Parse(Item.DocumentInstanceId);
        if (_currentPageId is null)
        {
            Status = "请先加载需要编辑的页面。";
            return;
        }

        Result<PageEditSession> session = await services.DocumentTrees.BeginPageEditAsync(docId, _currentPageId.Value);
        if (session.IsSuccess)
        {
            EditSessionId = session.Value.SessionId;
            _draftRevisionId = session.Value.DraftRevisionId;
            IsEditMode = true;
            IsSidebarOpen = true;
            SetActiveTool(PdfWorkspaceTool.Select);
            await ReloadAsync();
        }
        else
        {
            Status = $"无法进入编辑模式：{session.ErrorMessage}";
        }
    }

    private async Task SaveAndExitAsync()
    {
        if (EditSessionId is null)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        Result<DocumentTreeRevision> res = await services.DocumentTrees.CommitPageEditAsync(EditSessionId.Value);
        if (res.IsSuccess)
        {
            IsEditMode = false;
            _draftRevisionId = null;
            EditSessionId = null;
            ClearPendingBox();
            ClearSplit();
            ClearLocalOcrCandidate();
            await ReloadAsync();
        }
        else
        {
            Status = $"保存失败：{res.ErrorMessage}";
        }
    }

    private async Task CancelEditModeAsync()
    {
        if (EditSessionId is not null)
        {
            Result discarded =
                await (await _main.ServicesAsync()).DocumentTrees.DiscardPageEditAsync(EditSessionId.Value);
            if (discarded.IsFailure)
            {
                Status = $"放弃草稿失败：{discarded.ErrorMessage}";
                return;
            }
        }

        IsEditMode = false;
        _draftRevisionId = null;
        EditSessionId = null;
        ClearPendingBox();
        ClearSplit();
        ClearLocalOcrCandidate();
        await ReloadAsync();
    }

    private async Task LoadPageRevisionsAsync(HostServices services, DocumentInstanceId documentInstanceId,
        PageId pageId)
    {
        PageRevisions.Clear();
        Result<IReadOnlyList<DocumentTreeRevision>> revisions =
            await services.DocumentTrees.ListRevisionsAsync(documentInstanceId, pageId);
        if (revisions.IsFailure)
        {
            return;
        }

        List<PageRevisionViewModel> rows = revisions.Value
            .Select(revision => new PageRevisionViewModel(revision, ViewPageRevisionAsync, RevertPageRevisionAsync))
            .ToList();
        HistoryRowMetadata.Apply(
            rows,
            row => row.RevisionId.ToString(),
            row => row.RevertedFromRevisionId,
            (row, isNewest, isOldest, revertOffset) =>
            {
                row.IsNewest = isNewest;
                row.IsOldest = isOldest;
                row.RevertRowOffset = revertOffset;
            });

        foreach (PageRevisionViewModel row in rows)
        {
            PageRevisions.Add(row);
        }
    }

    private async Task ViewPageRevisionAsync(PageRevisionViewModel revision)
    {
        if (IsEditMode)
        {
            Status = "请先提交或放弃当前页面草稿，再查看历史版本。";
            return;
        }

        if (_currentPageId is null || string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
        {
            Status = "请先加载页面。";
            return;
        }

        BoundingBoxes.Clear();
        PreviewBlocks.Clear();
        ReadingScene = null;
        SelectedBox = null;
        IsViewingHistoricalRevision = true;
        _currentRevisionId = revision.RevisionId;
        await LoadBoxesIntoViewAsync(revision.RevisionId, false);
        Status = $"正在查看历史版本 {revision.RevisionId}（来源：{revision.Source}）。重新加载页面可返回当前版本。";
    }

    private async Task RevertPageRevisionAsync(PageRevisionViewModel revision)
    {
        if (IsEditMode)
        {
            Status = "请先提交或放弃当前页面草稿，再恢复历史版本。";
            return;
        }

        if (_currentPageId is null || string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
        {
            Status = "请先加载页面。";
            return;
        }

        ConfirmDialogResult? choice = await _main.Dialogs.ShowDialogAsync<ConfirmDialogResult>(
            new ConfirmDialogViewModel(
                "恢复历史版本",
                $"确定要将此页面恢复到版本 {revision.RevisionId} 吗？这将创建一个新的提交，不会删除后续版本。",
                "恢复",
                confirmDanger: true));
        if (choice != ConfirmDialogResult.Confirm)
        {
            return;
        }

        HostServices services = await _main.ServicesAsync();
        DocumentInstanceId documentInstanceId = DocumentInstanceId.Parse(Item.DocumentInstanceId);
        Result<DocumentTreeRevision> result = await services.DocumentTrees.RevertToRevisionAsync(
            documentInstanceId, _currentPageId.Value, revision.RevisionId);
        if (result.IsFailure)
        {
            Status = $"恢复版本失败：{result.ErrorMessage}";
            return;
        }

        IsViewingHistoricalRevision = false;
        await RenderCurrentPageAsync();
        Status = $"已恢复到版本 {result.Value.TreeRevisionId}（来源：{result.Value.Source}）。";
    }

    // ── Whole-book reading mode (B1) ─────────────────────────────────────────────
    // The PDF workspace's secondary "read the whole book" surface. Entering swaps the
    // toolbar+canvas+sidebar layout for a single streaming read-only RichEditor that renders
    // the document's committed page content from the page the user was viewing, prepending the
    // earlier pages above the viewport so the reading flow stays anchored. Font family and size
    // are tuned live and persisted through MainWindowViewModel.SaveReadingFont.

    public const string SystemDefaultReadingFontLabel = "系统默认";

    // Initial window around the current page: enough context behind for the reader to scroll up,
    // a longer run ahead because reading is mostly forward. Scrolling near an edge pulls the next
    // batch of the same size, so a large book never compiles all at once.
    private const int BookReadingInitialBehind = 2;
    private const int BookReadingInitialAhead = 8;
    private const int BookReadingBatchSize = 8;

    /// <summary>Raised once when reading mode is entered, before any page arrives, so the view can
    /// reset the editor, apply the current font and zero the scroll offset. Also raised by
    /// <see cref="ReplayBookReading"/> when a recreated view re-attaches.</summary>
    public event Action? BookReadingStarted;

    /// <summary>Raised on the UI thread for each delivered page. The view appends non-prepended
    /// pages and front-inserts (with scroll compensation) the prepended ones; the view model owns
    /// the delivery order so page order is preserved without the view tracking block counts.</summary>
    public event Action<BookReadingPage>? BookReadingPageReady;

    /// <summary>Raised on the UI thread when the initial window has been fully delivered (inside
    /// the serial pipeline, before the prefetch guard lifts) and when <see cref="ReplayBookReading"/>
    /// finishes, so the view can snap the scroll offset to the start page it has been tracking.
    /// Anchoring while the guard is still up keeps the trailing layout scroll events from
    /// cascading backward prefetch requests to page zero.</summary>
    public event Action? BookReadingAnchorRequested;

    /// <summary>Test seam: when set, the workspace builds the reading stream from this factory
    /// instead of constructing a real <see cref="BookReadingStream"/> from HostServices. The
    /// substituted stream ignores the services argument, so a unit test can drive page ordering
    /// and progress text without opening a real database.</summary>
    internal Func<HostServices, IBookReadingStream>? BookReadingStreamFactory { get; set; }

    /// <summary>Test seam: overrides the page the reading session starts at. The workspace's own
    /// page index is private, so a test cannot otherwise enter reading mode on a non-zero start
    /// page. Null uses the workspace's current page.</summary>
    internal int? BookReadingStartPageOverride { get; set; }

    public AsyncCommand EnterBookReadingCommand { get; }
    public RelayCommand ExitBookReadingCommand { get; }
    public RelayCommand BookReadingResetFontSizeCommand { get; }

    [ObservableProperty] public partial bool IsBookReadingMode { get; private set; }

    [ObservableProperty] public partial string BookReadingProgressText { get; private set; } = string.Empty;

    /// <summary>Live reading font size in points, clamped to [10, 28]. Setting it persists
    /// immediately through <see cref="MainWindowViewModel.SaveReadingFont"/> so the choice
    /// survives a restart, and raises so the view re-stamps the editor runs.</summary>
    [ObservableProperty]
    public partial double BookReadingFontSize { get; set; }

    partial void OnBookReadingFontSizeChanged(double value)
    {
        double clamped = ReadingFontCatalog.ClampSize(value);
        if (Math.Abs(value - clamped) > double.Epsilon)
        {
            BookReadingFontSize = clamped;
            return;
        }

        if (!_isConstructing)
        {
            _main.SaveReadingFont(DisplayToPersistedFontFamily(BookReadingFontFamily), clamped);
        }

        Raise(nameof(BookReadingFontSizeText));
    }

    [ExcludeFromDerivedGeneration] public string BookReadingFontSizeText => $"{Math.Round(BookReadingFontSize):0}pt";

    /// <summary>Display label of the selected reading font family. The first entry of
    /// <see cref="BookReadingFontFamilies"/> is <see cref="SystemDefaultReadingFontLabel"/>
    /// and maps to the persisted empty string. Setting it persists immediately.</summary>
    [ObservableProperty]
    public partial string BookReadingFontFamily { get; set; } = string.Empty;

    partial void OnBookReadingFontFamilyChanged(string value)
    {
        string normalized = (value ?? string.Empty).Trim();
        if (value != normalized)
        {
            BookReadingFontFamily = normalized;
            return;
        }

        if (!_isConstructing)
        {
            _main.SaveReadingFont(DisplayToPersistedFontFamily(normalized), BookReadingFontSize);
        }
    }

    /// <summary>Picker source for the reading font family: the system-default label first, then
    /// the host's installed font family names de-duplicated and sorted. Built lazily on first
    /// access so the view model constructor never touches the Avalonia font manager.</summary>
    [ExcludeFromDerivedGeneration]
    public IReadOnlyList<string> BookReadingFontFamilies
    {
        get
        {
            _bookReadingFontFamilies ??= BuildBookReadingFontFamilies();
            return _bookReadingFontFamilies;
        }
    }

    [ExcludeFromDerivedGeneration] internal bool BookReadingInitialWindowPending => _bookReadingInitialWindowPending;

    private async Task EnterBookReadingAsync()
    {
        if (IsBookReadingMode)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
        {
            Status = "该题录没有可阅读的文档实例。";
            return;
        }

        _bookReadingCancellation?.Cancel();
        _bookReadingCancellation?.Dispose();
        CancellationTokenSource cancellation = new();
        _bookReadingCancellation = cancellation;
        CancellationToken token = cancellation.Token;

        IsBookReadingMode = true;
        BookReadingProgressText = "正在准备阅读视图...";
        // Reset the session cache before announcing the start so the view is empty and the only
        // pages it ever sees are the ones this session delivers or replays.
        _bookReadingDelivered.Clear();
        _bookReadingLoaded.Clear();
        BookReadingStarted?.Invoke();

        try
        {
            IBookReadingStream stream;
            if (BookReadingStreamFactory is { } factory)
            {
                // Test seam: the substituted stream ignores the services argument, so a unit test
                // can drive page ordering without opening a real database.
                stream = factory(null!);
            }
            else
            {
                HostServices services = await _main.ServicesAsync();
                stream = new BookReadingStream(services);
            }

            // The stream lives for the whole session: on-demand windows reuse it until exit, so
            // the cancellation source is not disposed here (ExitBookReading owns its lifetime).
            DocumentInstanceId documentInstanceId = DocumentInstanceId.Parse(Item.DocumentInstanceId);
            _bookReadingDocumentInstanceId = documentInstanceId;
            _bookReadingStream = stream;
            IReadOnlyList<int> indices = await stream.ListPageIndicesAsync(documentInstanceId, token)
                .ConfigureAwait(true);
            if (indices.Count == 0)
            {
                BookReadingProgressText = "该文档没有可阅读的页面。";
                return;
            }

            _bookReadingIndices = indices.ToArray();
            int start = Math.Clamp(BookReadingStartPageOverride ?? PageIndex, 0, _bookReadingIndices.Length - 1);
            _bookReadingStartIndex = start;
            _bookReadingLo = start;
            _bookReadingHi = start;
            int lo = Math.Max(0, start - BookReadingInitialBehind);
            int hi = Math.Min(_bookReadingIndices.Length - 1, start + BookReadingInitialAhead);

            // The view derives a prepend's insertion offset from the already-recorded page with
            // the next higher index, so the start page and everything after it must arrive first:
            // {start} ∪ (start..hi] ∪ [lo..start).
            List<int> ordered = [start];
            ordered.AddRange(Enumerable.Range(start + 1, Math.Max(0, hi - start)));
            ordered.AddRange(Enumerable.Range(lo, start - lo));
            _bookReadingPlannedLo = lo;
            _bookReadingPlannedHi = hi;
            _bookReadingInitialWindowPending = true;
            try
            {
                await EnqueueBookReadingBatchAsync(
                    stream,
                    documentInstanceId,
                    ordered,
                    token,
                    () => _bookReadingInitialWindowPending = false,
                    () => Dispatcher.UIThread.InvokeAsync(() =>
                    {
                        if (IsBookReadingMode)
                        {
                            BookReadingAnchorRequested?.Invoke();
                        }
                    }).GetTask()).ConfigureAwait(true);
            }
            finally
            {
                _bookReadingInitialWindowPending = false;
            }
        }
        catch (OperationCanceledException)
        {
            // Exiting reading mode cancels the in-flight load; progress is cleared by ExitBookReading.
        }
        catch (Exception exception) when (exception is not OperationCanceledException)
        {
            BookReadingProgressText = string.Empty;
            Status = $"阅读模式加载失败：{exception.Message}";
            _main.ReportError($"阅读模式加载失败：{exception.Message}");
        }
    }

    /// <summary>Loads the pages in the caller-supplied order, delivering each to the view as it
    /// finishes. Order is the caller's responsibility: a prepend must not be delivered before the
    /// page that follows it, or the view cannot compute its insertion offset. Batches are
    /// serialized sequentially through Rx Concat so batches never overlap.</summary>
    private async Task LoadBookReadingPagesAsync(
        IBookReadingStream stream,
        DocumentInstanceId documentInstanceId,
        IEnumerable<int> orderedIndices,
        CancellationToken token)
    {
        foreach (int pageIndex in orderedIndices)
        {
            token.ThrowIfCancellationRequested();
            if (_bookReadingLoaded.Contains(pageIndex))
            {
                continue;
            }

            BookReadingPage page = await stream.LoadPageAsync(
                    documentInstanceId, pageIndex, _bookReadingIndices.Length, token)
                .ConfigureAwait(true);
            await DeliverBookReadingPageAsync(page).ConfigureAwait(true);
            _bookReadingLo = Math.Min(_bookReadingLo, page.PageIndex);
            _bookReadingHi = Math.Max(_bookReadingHi, page.PageIndex);
        }
    }

    private async Task DeliverBookReadingPageAsync(BookReadingPage raw)
    {
        BookReadingPage page = raw with { IsPrepend = raw.PageIndex < _bookReadingStartIndex };
        await Dispatcher.UIThread.InvokeAsync(() =>
        {
            if (!IsBookReadingMode)
            {
                return;
            }

            _bookReadingDelivered.Add(page);
            _bookReadingLoaded.Add(page.PageIndex);
            BookReadingProgressText = _bookReadingLoaded.Count >= _bookReadingIndices.Length
                ? string.Empty
                : $"已加载 {_bookReadingLoaded.Count}/{_bookReadingIndices.Length} 页";
            BookReadingPageReady?.Invoke(page);
        });
    }

    /// <summary>Loads the next window of pages after the highest page delivered so far. Called by
    /// the view when the reader scrolls near the bottom; a no-op at the end of the book, while the
    /// initial window is loading, or outside reading mode. Requests enqueue onto a serial (Concat)
    /// pipeline, so batches load one at a time without overlap or loss. Never throws: a failed batch
    /// sets the status line and leaves reading mode usable.</summary>
    public Task RequestBookReadingForwardAsync()
    {
        if (!IsBookReadingMode || _bookReadingInitialWindowPending || _bookReadingStream is not { } stream ||
            _bookReadingDocumentInstanceId is not { } documentInstanceId ||
            _bookReadingPlannedHi >= _bookReadingIndices.Length - 1)
        {
            return Task.CompletedTask;
        }

        int lo = _bookReadingPlannedHi + 1;
        int hi = Math.Min(_bookReadingIndices.Length - 1, _bookReadingPlannedHi + BookReadingBatchSize);
        _bookReadingPlannedHi = hi;

        List<int> ordered = [];
        for (int i = lo; i <= hi; i++)
        {
            ordered.Add(i);
        }

        CancellationToken token = _bookReadingCancellation?.Token ?? CancellationToken.None;
        return EnqueueBookReadingBatchAsync(stream, documentInstanceId, ordered, token);
    }

    /// <summary>Loads the window of pages before the lowest page delivered so far. Called by the
    /// view when the reader scrolls near the top; a no-op at the start of the book, while the
    /// initial window is loading, or outside reading mode. Requests enqueue onto the same serial
    /// (Concat) pipeline as forward loads.</summary>
    public Task RequestBookReadingBackwardAsync()
    {
        if (!IsBookReadingMode || _bookReadingInitialWindowPending || _bookReadingStream is not { } stream ||
            _bookReadingDocumentInstanceId is not { } documentInstanceId ||
            _bookReadingPlannedLo <= 0)
        {
            return Task.CompletedTask;
        }

        int hi = _bookReadingPlannedLo - 1;
        int lo = Math.Max(0, _bookReadingPlannedLo - BookReadingBatchSize);
        _bookReadingPlannedLo = lo;

        List<int> ordered = [];
        for (int i = lo; i <= hi; i++)
        {
            ordered.Add(i);
        }

        CancellationToken token = _bookReadingCancellation?.Token ?? CancellationToken.None;
        return EnqueueBookReadingBatchAsync(stream, documentInstanceId, ordered, token);
    }

    private Task EnqueueBookReadingBatchAsync(
        IBookReadingStream stream,
        DocumentInstanceId documentInstanceId,
        IReadOnlyList<int> orderedIndices,
        CancellationToken callerToken,
        Action? onCompleted = null,
        Func<Task>? onSuccessAsync = null)
    {
        if (!IsBookReadingMode)
        {
            return Task.CompletedTask;
        }

        TaskCompletionSource tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);
        CancellationToken sessionToken = _bookReadingCancellation?.Token ?? CancellationToken.None;

        _bookReadingSubject.OnNext(async streamToken =>
        {
            if (!IsBookReadingMode || sessionToken.IsCancellationRequested || callerToken.IsCancellationRequested)
            {
                try
                {
                    onCompleted?.Invoke();
                }
                finally
                {
                    tcs.TrySetCanceled();
                }

                return;
            }

            using CancellationTokenSource linked =
                CancellationTokenSource.CreateLinkedTokenSource(streamToken, sessionToken, callerToken);
            try
            {
                await LoadBookReadingPagesAsync(stream, documentInstanceId, orderedIndices, linked.Token)
                    .ConfigureAwait(true);
                if (onSuccessAsync is not null)
                {
                    await onSuccessAsync().ConfigureAwait(true);
                }

                onCompleted?.Invoke();
                tcs.TrySetResult();
            }
            catch (OperationCanceledException)
            {
                onCompleted?.Invoke();
                tcs.TrySetCanceled();
            }
            catch (Exception ex)
            {
                Status = $"阅读模式加载失败：{ex.Message}";
                _main.ReportError($"阅读模式加载失败：{ex.Message}");
                onCompleted?.Invoke();
                tcs.TrySetResult();
            }
        });

        return tcs.Task;
    }

    /// <summary>Re-raises the start event and every page delivered so far in ascending page
    /// order, so a view recreated by a tab switch repopulates without a reload. In-flight loads
    /// keep delivering through the live events; the loaded-page set keeps late arrivals from
    /// duplicating what the replay already restored.</summary>
    public void ReplayBookReading()
    {
        if (!IsBookReadingMode)
        {
            return;
        }

        BookReadingStarted?.Invoke();
        foreach (BookReadingPage page in _bookReadingDelivered.OrderBy(page => page.PageIndex))
        {
            BookReadingPageReady?.Invoke(page);
        }

        BookReadingAnchorRequested?.Invoke();
    }

    private void ExitBookReading()
    {
        _bookReadingCancellation?.Cancel();
        _bookReadingCancellation?.Dispose();
        _bookReadingCancellation = null;
        _bookReadingStream = null;
        _bookReadingDocumentInstanceId = null;
        _bookReadingDelivered.Clear();
        _bookReadingLoaded.Clear();
        _bookReadingIndices = [];
        _bookReadingPlannedLo = 0;
        _bookReadingPlannedHi = 0;
        _bookReadingLo = 0;
        _bookReadingHi = 0;
        _bookReadingInitialWindowPending = false;
        BookReadingProgressText = string.Empty;
        IsBookReadingMode = false;
    }

    /// <summary>Exits reading mode and navigates the PDF workbench to the given zero-based page,
    /// so the user can fix what they spotted while reading. Invoked from the rail badges.</summary>
    internal async Task ExitBookReadingToPageAsync(int pageIndex)
    {
        ExitBookReading();
        await GoToPageAsync(pageIndex + 1);
    }

    private static IReadOnlyList<string> BuildBookReadingFontFamilies()
    {
        List<string> names = [SystemDefaultReadingFontLabel];
        try
        {
            List<string> systemFonts = FontManager.Current.SystemFonts
                .Select(static font => font.Name)
                .Where(static name => !string.IsNullOrWhiteSpace(name))
                .Distinct(StringComparer.Ordinal)
                .OrderBy(static name => name, StringComparer.Ordinal)
                .ToList();
            names.AddRange(systemFonts);
        }
        catch (InvalidOperationException)
        {
            // Headless or uninitialized platforms expose no font manager; the system-default
            // entry stays as the only choice until the UI host initializes Avalonia.
        }

        return names.AsReadOnly();
    }

    // The persisted form uses the empty string for the system default; the view binds the combo
    // box to the localized label, so the two helpers below cross-map at the read/write boundaries.
    private static string FamilyToDisplay(string? persisted)
    {
        return string.IsNullOrWhiteSpace(persisted) ? SystemDefaultReadingFontLabel : persisted!.Trim();
    }

    private static string DisplayToPersistedFontFamily(string? display)
    {
        if (string.IsNullOrWhiteSpace(display) ||
            string.Equals(display, SystemDefaultReadingFontLabel, StringComparison.Ordinal))
        {
            return string.Empty;
        }

        return display.Trim();
    }

    // Reading-view media previews come from file assets: MediaBoxPayload.AssetId is expected to be a
    // FileAssetId, which resolves to the asset's original path. The file is decoded to a modest width
    // off the UI thread because the sidebar scales it down anyway. Any asset id that is not a file
    // asset id, or whose file is missing or not a decodable image, yields no preview and the reading
    // view keeps its placeholder card.
    private sealed class FileAssetMediaImageLoader(MainWindowViewModel main) : IMediaImageLoader
    {
        private const int PreviewDecodeWidth = 800;

        public async Task<IImage?> LoadAsync(string assetId, CancellationToken cancellationToken)
        {
            if (!Guid.TryParse(assetId, out Guid parsed))
            {
                return null;
            }

            HostServices services = await main.ServicesAsync();
            Result<FileAsset> asset = await services.Files.GetFileAssetAsync(
                new FileAssetId(parsed), cancellationToken);
            if (asset.IsFailure || !File.Exists(asset.Value.OriginalPath))
            {
                return null;
            }

            string path = asset.Value.OriginalPath;
            try
            {
                return await Task.Run(
                    () =>
                    {
                        using FileStream stream = File.OpenRead(path);
                        return (IImage?)Bitmap.DecodeToWidth(
                            stream, PreviewDecodeWidth, BitmapInterpolationMode.HighQuality);
                    },
                    cancellationToken);
            }
            catch (Exception exception) when (exception is not OperationCanceledException)
            {
                // A file asset that is not a decodable image simply has no preview.
                return null;
            }
        }
    }
}
