using Avalonia;
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
using Patchouli.UI.Services;

namespace Patchouli.UI.ViewModels;

public sealed class PdfWorkspaceViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private int _pageIndex;
    private int _pageCount;
    private int _lastNavigationDirection;
    private CancellationTokenSource? _prefetchCancellation;
    private int _widthPixels;
    private int _heightPixels;
    private int _renderGeneration;
    private bool _isEditMode;
    private bool _isSidebarOpen;
    private PdfWorkspaceTool _activeTool = PdfWorkspaceTool.Select;
    private PdfBBoxViewModel? _selectedBox;
    private DocumentTreeRevisionId? _currentRevisionId;
    private DocumentTreeRevisionId? _draftRevisionId;
    private PageEditSessionId? _editSessionId;
    private PageId? _currentPageId;
    private IReadOnlyList<DocumentBox> _loadedBoxes = [];
    private IReadOnlyList<Page> _pages = [];

    private readonly Dictionary<DocumentBoxId, (int PageIndex, DocumentBoxId HeadBoxId)>
        _crossPageContinuationSources = [];

    private readonly HashSet<DocumentBoxId> _collapsedBoxIds = [];
    private bool _isDrawing;
    private Point _selectionStartPoint;
    private NormalizedBBox? _pendingBBox;
    private string _newBoxText = string.Empty;
    private string _newBoxType = DocumentBoxType.Text;
    private int _newHeadingLevel = 1;
    private string _newCodeLanguage = string.Empty;
    private DocumentBox? _splitSource;
    private NormalizedBBox? _splitFirstBBox;
    private NormalizedBBox? _splitSecondBBox;
    private string _splitFirstText = string.Empty;
    private string _splitSecondText = string.Empty;
    private OcrRegionCandidate? _localOcrCandidate;
    private DocumentBoxId? _localOcrTargetBoxId;
    private string _localOcrSourceText = string.Empty;
    private DocumentBoxId? _previewSelectedBoxId;
    private DocumentBox[] _pendingMergeBoxes = [];
    private string _mergeText = string.Empty;
    private string _sourceValidationState = SourceValidationStatus.Unverified;
    private string? _sourceWarning;

    public PdfWorkspaceViewModel(MainWindowViewModel main, LibraryItemViewModel item)
    {
        _main = main;
        Item = item;
        PreviousPageCommand = new AsyncCommand(PreviousPageAsync);
        NextPageCommand = new AsyncCommand(NextPageAsync);
        ReloadCommand = new AsyncCommand(ReloadAsync);
        ZoomInCommand = new AsyncCommand(() =>
        {
            SetZoom(Zoom + 0.1);
            return Task.CompletedTask;
        });
        ZoomOutCommand = new AsyncCommand(() =>
        {
            SetZoom(Zoom - 0.1);
            return Task.CompletedTask;
        });

        EnterEditModeCommand = new AsyncCommand(EnterEditModeAsync);
        SaveAndExitCommand = new AsyncCommand(SaveAndExitAsync);
        CancelEditModeCommand = new AsyncCommand(CancelEditModeAsync);
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
        InsertPendingBoxCommand = new AsyncCommand(PersistStagingBoxAsync);
        CancelPendingBoxCommand = new AsyncCommand(() =>
        {
            ClearPendingBox();
            return Task.CompletedTask;
        });
        RunPendingOcrPrefillCommand = new AsyncCommand(RunPendingOcrPrefillAsync);
        SplitSelectedCommand = new AsyncCommand(SplitSelectedAsync);
        ConfirmSplitCommand = new AsyncCommand(ConfirmSplitAsync);
        CancelSplitCommand = new AsyncCommand(() =>
        {
            ClearSplit();
            return Task.CompletedTask;
        });
        RunLocalOcrCommand = new AsyncCommand(RunLocalOcrAsync);
        AcceptLocalOcrCommand = new AsyncCommand(AcceptLocalOcrAsync);
        RejectLocalOcrCommand = new AsyncCommand(() =>
        {
            ClearLocalOcrCandidate();
            Status = "已拒绝局部 OCR 候选结果；未写入识别记录或暂存树。";
            return Task.CompletedTask;
        });
        RunLogicalPageOcrCommand = new AsyncCommand(RunLogicalPageOcrAsync);
        RunCurrentPageOcrCommand = new AsyncCommand(RunCurrentPageOcrAsync);
        RunDocumentOcrCommand = new AsyncCommand(RunDocumentOcrAsync);
        CopyMarkdownCommand = new AsyncCommand(CopyMarkdownAsync);
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
        BoundingBoxes.CollectionChanged += (_, _) => Raise(nameof(HasNoBoundingBoxes));
        PreviewBlocks.CollectionChanged += (_, _) => Raise(nameof(HasNoPreviewBlocks));
    }

    public Bitmap? Image { get; private set; }
    public LibraryItemViewModel Item { get; }
    public bool HasImage => Image is not null;
    public bool HasNoImage => Image is null;
    public bool IsBusy { get; private set; }
    private string _status = "选择题录后可预览 PDF。";

    public string Status
    {
        get => _status;
        set
        {
            if (_status == value)
            {
                return;
            }

            _status = value;
            Raise();
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
    }

    public string PageNumberText => _pageCount == 0 ? "-" : (_pageIndex + 1).ToString();
    public string PageTotalText => _pageCount == 0 ? "/ -" : $"/ {_pageCount}";
    public string ZoomText => $"{Math.Round(Zoom * 100):0}%";
    public double Zoom { get; private set; } = 1.0;
    public double ActualWidthPixels => _widthPixels;
    public double ActualHeightPixels => _heightPixels;

    /// <summary>
    /// Last-known source validation state for the currently rendered page (AC16). It stays
    /// <see cref="SourceValidationStatus.Unverified"/> until a render lazily validates the
    /// source, transitions through <see cref="SourceValidationStatus.Validating"/> only while a
    /// render that may validate is in flight, and then settles on
    /// <see cref="SourceValidationStatus.Current"/>, <see cref="SourceValidationStatus.Changed"/>
    /// or <see cref="SourceValidationStatus.Unavailable"/>.
    /// </summary>
    public string SourceValidationState
    {
        get => _sourceValidationState;
        private set
        {
            if (_sourceValidationState == value)
            {
                return;
            }

            _sourceValidationState = value;
            Raise();
            Raise(nameof(IsSourceValidating));
            Raise(nameof(HasSourceWarning));
        }
    }

    /// <summary>True only while a render that may lazily validate the source is in flight.</summary>
    public bool IsSourceValidating => _sourceValidationState == SourceValidationStatus.Validating;

    /// <summary>Distinct source warning (e.g. source_changed/bbox_basis_stale) for the page.</summary>
    public string? SourceWarning
    {
        get => _sourceWarning;
        private set
        {
            if (_sourceWarning == value)
            {
                return;
            }

            _sourceWarning = value;
            Raise();
            Raise(nameof(HasSourceWarning));
        }
    }

    /// <summary>True when the source warning is present and validation is not still running.</summary>
    public bool HasSourceWarning => !string.IsNullOrWhiteSpace(SourceWarning) && !IsSourceValidating;

    public bool IsEditMode
    {
        get => _isEditMode;
        private set
        {
            if (_isEditMode == value)
            {
                return;
            }

            _isEditMode = value;
            Raise();
        }
    }

    public PageEditSessionId? EditSessionId => _editSessionId;

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

    public bool IsNewBoxPending => _pendingBBox is not null;
    public bool IsSplitPending => _splitSource is not null;
    public bool CanConfirmSplit => _splitFirstBBox is not null && _splitSecondBBox is not null;

    public string SplitStepText => _splitFirstBBox is null
        ? "请在页面框出第一个替代区域。"
        : _splitSecondBBox is null
            ? "请在页面框出第二个替代区域。"
            : "两个区域已就绪；检查两份内容后确认拆分。";

    public bool HasCandidate => _localOcrCandidate is not null;
    public string LocalOcrSourceText => _localOcrSourceText;
    public bool IsMergePending => _pendingMergeBoxes.Length > 0;

    public string MergeText
    {
        get => _mergeText;
        set
        {
            if (_mergeText != value)
            {
                _mergeText = value;
                Raise();
            }
        }
    }

    public string OcrPresetStatusText => "局部与页面 OCR 使用当前库的 MinerU preset。";

    public string SplitFirstText
    {
        get => _splitFirstText;
        set
        {
            if (_splitFirstText != value)
            {
                _splitFirstText = value;
                Raise();
            }
        }
    }

    public string SplitSecondText
    {
        get => _splitSecondText;
        set
        {
            if (_splitSecondText != value)
            {
                _splitSecondText = value;
                Raise();
            }
        }
    }

    public string NewBoxText
    {
        get => _newBoxText;
        set
        {
            if (_newBoxText != value)
            {
                _newBoxText = value;
                Raise();
            }
        }
    }

    public string NewBoxType
    {
        get => _newBoxType;
        set
        {
            if (_newBoxType != value)
            {
                _newBoxType = value;
                Raise();
                Raise(nameof(NewBoxIsTitle));
                Raise(nameof(NewBoxIsCode));
            }
        }
    }

    public bool NewBoxIsTitle => NewBoxType == DocumentBoxType.Title;
    public bool NewBoxIsCode => NewBoxType is DocumentBoxType.Code or DocumentBoxType.Algorithm;

    public int NewHeadingLevel
    {
        get => _newHeadingLevel;
        set
        {
            int normalized = Math.Clamp(value, 1, 6);
            if (_newHeadingLevel != normalized)
            {
                _newHeadingLevel = normalized;
                Raise();
            }
        }
    }

    public string NewCodeLanguage
    {
        get => _newCodeLanguage;
        set
        {
            if (_newCodeLanguage != value)
            {
                _newCodeLanguage = value;
                Raise();
            }
        }
    }

    public bool IsSidebarOpen
    {
        get => _isSidebarOpen;
        set
        {
            if (_isSidebarOpen == value)
            {
                return;
            }

            _isSidebarOpen = value;
            Raise();
            Raise(nameof(SidebarMaxWidth));
            Raise(nameof(SidebarMinWidth));
        }
    }

    public double SidebarMaxWidth => _isSidebarOpen ? 800.0 : 0.0;
    public double SidebarMinWidth => _isSidebarOpen ? 200.0 : 0.0;
    private double _selectionLeft;
    private double _selectionTop;
    private double _selectionWidth;
    private double _selectionHeight;

    public PdfWorkspaceTool ActiveTool
    {
        get => _activeTool;
        private set
        {
            if (_activeTool == value)
            {
                return;
            }

            _activeTool = value;
            Raise();
            Raise(nameof(IsSelectToolActive));
            Raise(nameof(IsMarqueeToolActive));
            Raise(nameof(IsCreateBoxToolActive));
            Raise(nameof(IsRectToolActive));
        }
    }

    public bool IsSelectToolActive => ActiveTool == PdfWorkspaceTool.Select;
    public bool IsMarqueeToolActive => ActiveTool == PdfWorkspaceTool.MarqueeSelect;
    public bool IsCreateBoxToolActive => ActiveTool == PdfWorkspaceTool.CreateBox;
    public bool IsRectToolActive => ActiveTool != PdfWorkspaceTool.Select;

    private void SetActiveTool(PdfWorkspaceTool tool)
    {
        ActiveTool = tool;
    }

    public bool IsDrawing
    {
        get => _isDrawing;
        private set
        {
            if (_isDrawing == value)
            {
                return;
            }

            _isDrawing = value;
            Raise();
            Raise(nameof(SelectionVisible));
        }
    }

    public double SelectionLeft
    {
        get => _selectionLeft;
        private set
        {
            if (_selectionLeft == value)
            {
                return;
            }

            _selectionLeft = value;
            Raise();
        }
    }

    public double SelectionTop
    {
        get => _selectionTop;
        private set
        {
            if (_selectionTop == value)
            {
                return;
            }

            _selectionTop = value;
            Raise();
        }
    }

    public double SelectionWidth
    {
        get => _selectionWidth;
        private set
        {
            if (_selectionWidth == value)
            {
                return;
            }

            _selectionWidth = value;
            Raise();
            Raise(nameof(SelectionVisible));
        }
    }

    public double SelectionHeight
    {
        get => _selectionHeight;
        private set
        {
            if (_selectionHeight == value)
            {
                return;
            }

            _selectionHeight = value;
            Raise();
            Raise(nameof(SelectionVisible));
        }
    }

    public bool SelectionVisible => (IsDrawing || IsNewBoxPending) && SelectionWidth > 0 && SelectionHeight > 0;

    public System.Collections.ObjectModel.ObservableCollection<PdfBBoxViewModel> BoundingBoxes { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<PdfBBoxViewModel> SelectedBoxes { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<SplitDraftBoxViewModel> SplitDraftBoxes { get; } =
        new();

    public System.Collections.ObjectModel.ObservableCollection<MarkdownPreviewBlockViewModel> PreviewBlocks { get; } =
        new();

    public System.Collections.ObjectModel.ObservableCollection<PdfBBoxViewModel> CandidateBoxes { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<PdfOverlapMarkerViewModel> OverlapMarkers { get; } =
        new();

    public System.Collections.ObjectModel.ObservableCollection<PdfContinuationLinkViewModel>
        ContinuationLinks { get; } =
        new();

    public System.Collections.ObjectModel.ObservableCollection<PdfCrossPageContinuationViewModel>
        CrossPageContinuationMarkers { get; } = new();

    // Visible rows of the edit-mode box tree: BoundingBoxes minus collapsed subtrees.
    public System.Collections.ObjectModel.ObservableCollection<PdfBBoxViewModel> TreeBoxes { get; } = new();

    public bool HasNoBoundingBoxes => BoundingBoxes.Count == 0;
    public bool HasNoPreviewBlocks => PreviewBlocks.Count == 0;
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
            }
            else
            {
                CreateBBoxFromSelection();
            }
        }

        if (_pendingBBox is null)
        {
            SelectionWidth = 0;
            SelectionHeight = 0;
        }
    }

    private void ApplyMarqueeSelection(bool additive)
    {
        if (_widthPixels <= 0 || _heightPixels <= 0)
        {
            return;
        }

        double x = SelectionLeft / _widthPixels;
        double y = SelectionTop / _heightPixels;
        double width = SelectionWidth / _widthPixels;
        double height = SelectionHeight / _heightPixels;
        List<PdfBBoxViewModel> hits = BoundingBoxes.Where(box =>
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
        if (_draftRevisionId is null || _currentPageId is null || _widthPixels <= 0 || _heightPixels <= 0)
        {
            Status = "请先进入编辑模式并加载页面。";
            return;
        }

        double x = Math.Clamp(SelectionLeft / _widthPixels, 0, 1);
        double y = Math.Clamp(SelectionTop / _heightPixels, 0, 1);
        double width = Math.Clamp(SelectionWidth / _widthPixels, 0.0001, 1 - x);
        double height = Math.Clamp(SelectionHeight / _heightPixels, 0.0001, 1 - y);
        if (_splitSource is not null)
        {
            if (_splitFirstBBox is null)
            {
                _splitFirstBBox = new NormalizedBBox(x, y, width, height);
                SplitDraftBoxes.Add(new SplitDraftBoxViewModel(
                    x * _widthPixels, y * _heightPixels, width * _widthPixels, height * _heightPixels, "1"));
            }
            else
            {
                _splitSecondBBox = new NormalizedBBox(x, y, width, height);
                SplitDraftBoxes.Add(new SplitDraftBoxViewModel(
                    x * _widthPixels, y * _heightPixels, width * _widthPixels, height * _heightPixels, "2"));
            }

            Raise(nameof(CanConfirmSplit));
            Raise(nameof(SplitStepText));
            Status = SplitStepText;
            return;
        }

        _pendingBBox = new NormalizedBBox(x, y, width, height);
        NewBoxText = string.Empty;
        NewBoxType = DocumentBoxType.Text;
        NewHeadingLevel = 1;
        NewCodeLanguage = string.Empty;
        Raise(nameof(IsNewBoxPending));
        Raise(nameof(SelectionVisible));
        Status = _loadedBoxes.Count == 0
            ? "填写类型和内容后插入第一个根边界框。"
            : "填写类型和内容后插入到边界框列表末尾。";
        _ = OpenNewBoxEditorAsync();
    }

    private async Task PersistStagingBoxAsync()
    {
        if (_editSessionId is null || _draftRevisionId is null || _currentPageId is null)
        {
            return;
        }

        if (_pendingBBox is null)
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

            result = await editor.InsertLogicalPageAsync(_editSessionId.Value, after, _pendingBBox.Value);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(NewBoxText))
            {
                Status = "新建叶子边界框必须填写有效内容。";
                return;
            }

            result = await editor.DrawAndInsertLeafAsync(
                _editSessionId.Value,
                new InsertLeafCommand(null, after, NewBoxType, null, null, _pendingBBox.Value,
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

    private async Task<OcrPresetId?> ResolveOcrPresetIdAsync()
    {
        try
        {
            return await LibraryShellViewModel.EnsureMinerUPresetAsync(await _main.ServicesAsync());
        }
        catch (Exception exception)
        {
            Status = $"OCR preset 不可用：{exception.Message}";
            return null;
        }
    }

    private async Task RunOcrModalAsync(string title, string initialStatus, Func<Task<Result>> operation)
    {
        await _main.ModalOperations.RunAsync(
            new ModalOperationOptions(title, initialStatus),
            async _ => await Dispatcher.UIThread.InvokeAsync(operation));
    }

    private async Task RunPendingOcrPrefillAsync()
    {
        if (_pendingBBox is null || _currentPageId is null || string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
        {
            Status = "请先框出区域，再运行 OCR 预填。";
            return;
        }

        OcrPresetId? presetId = await ResolveOcrPresetIdAsync();
        if (presetId is null)
        {
            return;
        }

        await RunOcrModalAsync("局部 OCR 预填", "正在识别所选区域...", async () =>
        {
            Result<OcrRegionCandidate> candidate = await (await _main.ServicesAsync()).Ocr
                .RecognizeRegionCandidateAsync(
                    DocumentInstanceId.Parse(Item.DocumentInstanceId), presetId.Value, _currentPageId.Value,
                    _pendingBBox.Value);
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
        if (_editSessionId is null || SelectedBox is null || SelectedBox.IsLogicalPage)
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
        _splitSource = original;
        SplitFirstText = parts.Length > 1 ? parts[0] : text[..(text.Length / 2)];
        SplitSecondText = parts.Length > 1 ? string.Join("\n\n", parts.Skip(1)) : text[(text.Length / 2)..];
        SetActiveTool(PdfWorkspaceTool.CreateBox);
        Raise(nameof(IsSplitPending));
        Raise(nameof(CanConfirmSplit));
        Raise(nameof(SplitStepText));
        Status = SplitStepText;
        await Task.CompletedTask;
    }

    private async Task ConfirmSplitAsync()
    {
        if (_editSessionId is null || _splitSource is null || _splitFirstBBox is null || _splitSecondBBox is null ||
            string.IsNullOrWhiteSpace(SplitFirstText) || string.IsNullOrWhiteSpace(SplitSecondText))
        {
            Status = "必须先框出两个替代区域，并分别填写非空内容。";
            return;
        }

        Result<IReadOnlyList<DocumentBox>> result =
            await (await _main.ServicesAsync()).DocumentTreeEditor.SplitLeafAsync(
                _editSessionId.Value,
                new SplitLeafCommand(_splitSource.BoxId, _splitFirstBBox.Value,
                    CreatePayload(_splitSource.BoxType, SplitFirstText, _splitSource.Payload), _splitSecondBBox.Value,
                    CreatePayload(_splitSource.BoxType, SplitSecondText, _splitSource.Payload)));
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

        OcrPresetId? presetId = await ResolveOcrPresetIdAsync();
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

            _localOcrCandidate = candidate.Value;
            _localOcrTargetBoxId = source.BoxId;
            _localOcrSourceText = PayloadTextFor(source) ?? string.Empty;
            Raise(nameof(LocalOcrSourceText));
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
                false), _widthPixels, _heightPixels, true));

            Raise(nameof(HasCandidate));
            Status = "局部 OCR 候选结果已生成；这是一个短生命周期的完整内容差异，不会写入识别记录或暂存树。";
            return Result.Success();
        });
        if (_localOcrCandidate is not null && _localOcrTargetBoxId is { } targetId &&
            BoundingBoxes.FirstOrDefault(box => box.BoxId == targetId) is { } target)
        {
            await OpenBoxEditorAsync(target);
        }
    }

    private async Task AcceptLocalOcrAsync()
    {
        if (_editSessionId is null || _localOcrTargetBoxId is null || _localOcrCandidate is null)
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
            _editSessionId.Value, target.BoxId,
            new LocalOcrCandidate(_localOcrCandidate.BoxType, _localOcrCandidate.Payload,
                _localOcrCandidate.HeadingLevel));
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
        _localOcrCandidate = null;
        _localOcrTargetBoxId = null;
        _localOcrSourceText = string.Empty;
        Raise(nameof(LocalOcrSourceText));
        Raise(nameof(HasCandidate));
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

        OcrPresetId? presetId = await ResolveOcrPresetIdAsync();
        if (presetId is null)
        {
            return;
        }

        await RunOcrModalAsync("逻辑页 OCR", "正在按逻辑页识别本页...", async () =>
        {
            AppServices services = await _main.ServicesAsync();
            Result<LogicalPageOcrResult> result = await services.LogicalPageOcr.RunAsync(
                DocumentInstanceId.Parse(Item.DocumentInstanceId), presetId.Value,
                _currentPageId.Value, OrderSiblings(logicalPages)
                    .Select(box => new LogicalPageOcrTarget(box.BoxId, box.BBox)).ToArray());
            if (result.IsFailure)
            {
                Status = $"逻辑页 OCR 失败：{result.ErrorMessage}";
                return Result.Failure(result.ErrorCode!, result.ErrorMessage!);
            }

            Status = $"逻辑页 OCR 已合成为暂存树 {result.Value.StagingTreeRevisionId}；区域已映射回物理页。";
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

        OcrPresetId? presetId = await ResolveOcrPresetIdAsync();
        if (presetId is null)
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
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

        OcrPresetId? presetId = await ResolveOcrPresetIdAsync();
        if (presetId is null)
        {
            return;
        }

        await RunOcrModalAsync("本页 OCR", "正在识别当前物理页...", async () =>
        {
            AppServices services = await _main.ServicesAsync();
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
                ? $"本页 OCR 已按 {result.Value.RunIds.Count} 个逻辑页生成暂存树 {result.Value.StagingTreeRevisionId}。"
                : $"本页整页 OCR 已生成暂存树 {result.Value.StagingTreeRevisionId}。";
            return Result.Success();
        });
    }

    private static async Task<Result<LogicalDocumentOcrPagePlan>> CreatePageOcrPlanAsync(
        AppServices services,
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
        _splitSource = null;
        _splitFirstBBox = null;
        _splitSecondBBox = null;
        SplitDraftBoxes.Clear();
        SplitFirstText = string.Empty;
        SplitSecondText = string.Empty;
        Raise(nameof(IsSplitPending));
        Raise(nameof(CanConfirmSplit));
        Raise(nameof(SplitStepText));
    }

    private async Task MergeSelectedAsync()
    {
        if (_editSessionId is null)
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

        _pendingMergeBoxes = mergeBoxes;
        MergeText = string.Join("\n\n", mergeBoxes.Select(PayloadTextFor)
            .Where(value => !string.IsNullOrWhiteSpace(value)));
        Raise(nameof(IsMergePending));
        Status = "请检查并编辑合并结果内容，然后显式确认。";
        await Task.CompletedTask;
    }

    private async Task ConfirmMergeAsync()
    {
        if (_editSessionId is null || _pendingMergeBoxes.Length < 2 || string.IsNullOrWhiteSpace(MergeText))
        {
            Status = "合并结果必须提供非空内容。";
            return;
        }

        DocumentBox selected = _pendingMergeBoxes[0];
        Result<DocumentBox> result = await (await _main.ServicesAsync()).DocumentTreeEditor.MergeLeavesAsync(
            _editSessionId.Value,
            new MergeLeavesCommand(_pendingMergeBoxes.Select(box => box.BoxId).ToArray(), CreatePayload(
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
        _pendingMergeBoxes = [];
        MergeText = string.Empty;
        Raise(nameof(IsMergePending));
    }

    private async Task DeleteSelectedAsync()
    {
        PdfBBoxViewModel[] targets = SelectedBoxes.Count > 0
            ? SelectedBoxes.ToArray()
            : SelectedBox is { } single
                ? [single]
                : [];
        if (targets.Length == 0)
        {
            Status = "请先选择要删除的边界框。";
            return;
        }

        foreach (PdfBBoxViewModel target in targets)
        {
            await target.DeleteCommand.ExecuteAsync();
        }
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
        if (_editSessionId is null || SelectedBoxes.Count == 0)
        {
            Status = "请先选择要移动的边界框。";
            return;
        }

        PageEditSessionId sessionId = _editSessionId.Value;
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
        if (_editSessionId is null || movingView.BoxId == targetView.BoxId)
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
                _editSessionId.Value, new MoveBoxCommand(moving.BoxId, parent, after));
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
        if (_editSessionId is null || SelectedBoxes.Count == 0)
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
                _editSessionId.Value, new MoveBoxCommand(box.BoxId, newParent.BoxId, after));
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
        if (_editSessionId is null || SelectedBoxes.Count == 0)
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
                _editSessionId.Value, new MoveBoxCommand(box.BoxId, parentBox.ParentBoxId, after));
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
        _pendingBBox = null;
        NewBoxText = string.Empty;
        SelectionWidth = 0;
        SelectionHeight = 0;
        Raise(nameof(IsNewBoxPending));
        Raise(nameof(SelectionVisible));
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
        _pageIndex = 0;
        _lastNavigationDirection = 0;
        _renderGeneration++;
        await RenderCurrentPageAsync();
    }

    public void Clear()
    {
        _prefetchCancellation?.Cancel();
        _prefetchCancellation = null;
        _ = ReleaseDocumentSessionAsync();
        PageEditSessionId? sessionId = _editSessionId;
        Image?.Dispose();
        Image = null;
        _pageIndex = 0;
        _pageCount = 0;
        _widthPixels = 0;
        _heightPixels = 0;
        _renderGeneration++;
        IsEditMode = false;
        IsSidebarOpen = false;
        SetActiveTool(PdfWorkspaceTool.Select);
        BoundingBoxes.Clear();
        TreeBoxes.Clear();
        OverlapMarkers.Clear();
        ContinuationLinks.Clear();
        CrossPageContinuationMarkers.Clear();
        _crossPageContinuationSources.Clear();
        _pages = [];
        PreviewBlocks.Clear();
        SelectedBox = null;
        _currentRevisionId = null;
        _draftRevisionId = null;
        _editSessionId = null;
        _loadedBoxes = [];
        _collapsedBoxIds.Clear();
        ClearSplit();
        ClearPendingBox();
        ClearLocalOcrCandidate();
        ClearSourceValidation();
        Status = "选择题录后可预览 PDF。";
        RaiseAll();
        if (sessionId is not null)
        {
            _ = DiscardClearedDraftAsync(sessionId.Value);
        }
    }

    private async Task PreviousPageAsync()
    {
        if (IsEditMode)
        {
            Status = "请先提交或放弃当前页面草稿，再切换页面。";
            return;
        }

        if (_pageIndex <= 0)
        {
            return;
        }

        _pageIndex--;
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

        if (_pageCount > 0 && _pageIndex >= _pageCount - 1)
        {
            return;
        }

        _pageIndex++;
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

        if (_pageCount <= 0)
        {
            return;
        }

        int target = Math.Clamp(pageNumber - 1, 0, _pageCount - 1);
        if (target == _pageIndex)
        {
            Raise(nameof(PageNumberText));
            return;
        }

        _lastNavigationDirection = target > _pageIndex ? 1 : target < _pageIndex ? -1 : 0;
        _pageIndex = target;
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
        Raise(nameof(IsSidebarOpen));
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
        Raise(nameof(Zoom));
        Raise(nameof(ZoomText));
    }

    private async Task RenderCurrentPageAsync()
    {
        int generation = ++_renderGeneration;
        Image?.Dispose();
        Image = null;
        _widthPixels = 0;
        IsBusy = true;
        BeginSourceValidation();
        Status = "正在渲染 PDF 预览...";
        RaiseAll();

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

            AppServices services = await _main.ServicesAsync();
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

            _pageCount = pages.Value.Count;
            _pages = pages.Value;
            if (_pageCount == 0)
            {
                ClearSourceValidation();
                Status = "该文档还没有页面记录。";
                return;
            }

            _pageIndex = Math.Clamp(_pageIndex, 0, _pageCount - 1);
            Page page = pages.Value[_pageIndex];
            _currentPageId = page.PageId;
            Result<PdfPagePixelBufferLease> preview = await services.PageRenders.RenderPreviewAsync(
                new PageRenderRequest(documentInstanceId, page.PageId, fileAssetId, 120,
                    Purpose: PageRenderPurpose.Preview));
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
            _widthPixels = raster.WidthPixels;
            _heightPixels = raster.HeightPixels;

            BoundingBoxes.Clear();
            PreviewBlocks.Clear();
            SelectedBox = null;
            if (_isEditMode && _draftRevisionId != null)
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
                    await LoadBoxesIntoViewAsync(rev.Value.TreeRevisionId, false);
                }
            }

            Status =
                $"{Item.Title} · 第 {_pageIndex + 1}/{_pageCount} 页 · {raster.WidthPixels}x{raster.HeightPixels} · {raster.RendererBasisVersion}";
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
                RaiseAll();
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
    private void SchedulePrefetchAsync(AppServices services, DocumentInstanceId documentInstanceId,
        FileAssetId? fileAssetId)
    {
        _prefetchCancellation?.Cancel();
        _prefetchCancellation?.Dispose();
        _prefetchCancellation = new CancellationTokenSource();
        CancellationToken token = _prefetchCancellation.Token;
        // Keep the window ordered and render it serially. Starting all neighbours at once can
        // put low-priority PDFium work ahead of the page the user has just requested. Identical
        // foreground/prefetch requests still merge in PageRenderService's in-flight cache.
        int[] targets = PrefetchWindow(_pageIndex, _pageCount, _lastNavigationDirection);
        _ = PrefetchWindowAsync(services, documentInstanceId, fileAssetId, targets, token);
    }

    private async Task PrefetchWindowAsync(AppServices services, DocumentInstanceId documentInstanceId,
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
        catch (OperationCanceledException)
        {
            // A newer page selection owns the next prefetch window.
        }
    }

    private async Task PrefetchPageAsync(AppServices services, DocumentInstanceId documentInstanceId,
        FileAssetId? fileAssetId, int pageIndex, CancellationToken cancellationToken)
    {
        try
        {
            if (pageIndex < 0 || pageIndex >= _pages.Count || pageIndex == _pageIndex)
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

    private async Task<FileAssetId?> ResolveFileAssetIdAsync(AppServices services,
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

    private async Task LoadBoxesIntoViewAsync(DocumentTreeRevisionId revisionId, bool isStaging)
    {
        _loadedBoxes = await LoadBoxesAsync(revisionId);
        int readingOrder = 0;
        foreach ((DocumentBox Box, int Depth) item in OrderedTree(_loadedBoxes))
        {
            DocumentBox box = item.Box;
            BoundingBoxes.Add(new PdfBBoxViewModel(
                _main, this, box, _widthPixels, _heightPixels, isStaging, ++readingOrder, item.Depth));
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
            AppServices services = await _main.ServicesAsync();
            DocumentInstanceId documentInstanceId = DocumentInstanceId.Parse(Item.DocumentInstanceId);
            for (int pageIndex = Math.Min(_pageIndex - 1, _pages.Count - 1);
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

        _pageIndex = target.PageIndex;
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
            AppServices services = await _main.ServicesAsync();
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
                            first, second, overlap.Intersection, _widthPixels, _heightPixels));
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
        Raise(nameof(HasNoBoundingBoxes));
    }

    private async Task LoadPreviewAsync(DocumentTreeRevisionId revisionId)
    {
        PreviewBlocks.Clear();
        AppServices services = await _main.ServicesAsync();
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

    private async Task EnterEditModeAsync()
    {
        if (string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
        {
            Status = "该题录没有可编辑的文档实例。";
            return;
        }

        AppServices services = await _main.ServicesAsync();
        DocumentInstanceId docId = DocumentInstanceId.Parse(Item.DocumentInstanceId);
        if (_currentPageId is null)
        {
            Status = "请先加载需要编辑的页面。";
            return;
        }

        Result<PageEditSession> session = await services.DocumentTrees.BeginPageEditAsync(docId, _currentPageId.Value);
        if (session.IsSuccess)
        {
            _editSessionId = session.Value.SessionId;
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
        if (_editSessionId is null)
        {
            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result<DocumentTreeRevision> res = await services.DocumentTrees.CommitPageEditAsync(_editSessionId.Value);
        if (res.IsSuccess)
        {
            IsEditMode = false;
            _draftRevisionId = null;
            _editSessionId = null;
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
        if (_editSessionId is not null)
        {
            Result discarded =
                await (await _main.ServicesAsync()).DocumentTrees.DiscardPageEditAsync(_editSessionId.Value);
            if (discarded.IsFailure)
            {
                Status = $"放弃草稿失败：{discarded.ErrorMessage}";
                return;
            }
        }

        IsEditMode = false;
        _draftRevisionId = null;
        _editSessionId = null;
        ClearPendingBox();
        ClearSplit();
        ClearLocalOcrCandidate();
        await ReloadAsync();
    }

    private void RaiseAll()
    {
        Raise(nameof(Image));
        Raise(nameof(HasImage));
        Raise(nameof(HasNoImage));
        Raise(nameof(IsBusy));
        Raise(nameof(Status));
        Raise(nameof(PageNumberText));
        Raise(nameof(PageTotalText));
        Raise(nameof(ActualWidthPixels));
        Raise(nameof(ActualHeightPixels));
    }
}
