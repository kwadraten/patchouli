using Avalonia;
using Avalonia.Media.Imaging;
using Avalonia.Platform;
using Dapper;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Documents;
using Patchouli.Core.Layout;
using Patchouli.Ocr;
using System.Linq;
using Microsoft.Data.Sqlite;
using Patchouli.Core.Results;

namespace Patchouli.UI.ViewModels;

public sealed class PdfWorkspaceViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private int _pageIndex;
    private int _pageCount;
    private int _widthPixels;
    private int _heightPixels;
    private int _renderGeneration;
    private bool _isEditMode;
    private bool _isSidebarOpen;
    private bool _isDrawToolActive;
    private PdfBBoxViewModel? _selectedBox;
    private DocumentTreeRevisionId? _currentRevisionId;
    private DocumentTreeRevisionId? _draftRevisionId;
    private PageEditSessionId? _editSessionId;
    private PageId? _currentPageId;
    private IReadOnlyList<DocumentBox> _loadedBoxes = [];
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
    private string _ocrPresetId = string.Empty;
    private OcrRegionCandidate? _localOcrCandidate;
    private DocumentBoxId? _localOcrTargetBoxId;
    private string _localOcrSourceText = string.Empty;
    private DocumentBoxId? _previewSelectedBoxId;
    private DocumentBox[] _pendingMergeBoxes = [];
    private string _mergeText = string.Empty;

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
            IsDrawToolActive = false;
            return Task.CompletedTask;
        });
        DrawToolCommand = new AsyncCommand(() =>
        {
            IsDrawToolActive = true;
            return Task.CompletedTask;
        });
        InsertPendingBoxCommand = new AsyncCommand(PersistStagingBoxAsync);
        CancelPendingBoxCommand = new AsyncCommand(() =>
        {
            ClearPendingBox();
            return Task.CompletedTask;
        });
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
            Status = "已拒绝局部 OCR candidate；未写入 OCR run 或 staging tree。";
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
        ? "请在页面画出第一个替代 bbox。"
        : _splitSecondBBox is null
            ? "请在页面画出第二个替代 bbox。"
            : "两个 bbox 已就绪；检查两份内容后确认拆分。";

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

    public string OcrPresetId
    {
        get => _ocrPresetId;
        set
        {
            if (_ocrPresetId != value)
            {
                _ocrPresetId = value;
                Raise();
            }
        }
    }

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
            }
        }
    }

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

    public bool IsDrawToolActive
    {
        get => _isDrawToolActive;
        private set
        {
            if (_isDrawToolActive == value)
            {
                return;
            }

            _isDrawToolActive = value;
            Raise();
        }
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

    public bool SelectionVisible => IsDrawing && SelectionWidth > 0 && SelectionHeight > 0;

    public System.Collections.ObjectModel.ObservableCollection<PdfBBoxViewModel> BoundingBoxes { get; } = new();

    public System.Collections.ObjectModel.ObservableCollection<MarkdownPreviewBlockViewModel> PreviewBlocks { get; } =
        new();

    public System.Collections.ObjectModel.ObservableCollection<PdfBBoxViewModel> CandidateBoxes { get; } = new();

    public bool HasNoBoundingBoxes => BoundingBoxes.Count == 0;
    public bool HasNoPreviewBlocks => PreviewBlocks.Count == 0;

    public PdfBBoxViewModel? SelectedBox
    {
        get => _selectedBox;
        set
        {
            if (_selectedBox == value)
            {
                return;
            }

            if (_selectedBox != null)
            {
                _selectedBox.IsSelected = false;
            }

            _selectedBox = value;
            if (_selectedBox != null)
            {
                _selectedBox.IsSelected = true;
            }

            if (_selectedBox is null || !_selectedBox.IsSuppressed)
            {
                _previewSelectedBoxId = _selectedBox?.BoxId;
            }

            foreach (MarkdownPreviewBlockViewModel block in PreviewBlocks)
            {
                block.IsSelected = block.BoxId == _previewSelectedBoxId;
            }

            Raise();
            SelectionChanged?.Invoke(_selectedBox);
        }
    }

    public event Action<PdfBBoxViewModel?>? SelectionChanged;

    public AsyncCommand PreviousPageCommand { get; }
    public AsyncCommand NextPageCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand ZoomInCommand { get; }
    public AsyncCommand ZoomOutCommand { get; }
    public AsyncCommand EnterEditModeCommand { get; }
    public AsyncCommand SaveAndExitCommand { get; }
    public AsyncCommand CancelEditModeCommand { get; }
    public AsyncCommand SelectToolCommand { get; }
    public AsyncCommand DrawToolCommand { get; }
    public AsyncCommand InsertPendingBoxCommand { get; }
    public AsyncCommand CancelPendingBoxCommand { get; }
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

    public void OnPointerPressed(double x, double y)
    {
        if (!IsEditMode || !IsDrawToolActive)
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

    public void OnPointerReleased()
    {
        if (!IsDrawing)
        {
            return;
        }

        IsDrawing = false;
        if (SelectionWidth > 5 && SelectionHeight > 5)
        {
            CreateBBoxFromSelection();
        }

        SelectionWidth = 0;
        SelectionHeight = 0;
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
            }
            else
            {
                _splitSecondBBox = new NormalizedBBox(x, y, width, height);
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
        Status = _loadedBoxes.Count == 0
            ? "填写类型和内容后插入第一个根 Box。"
            : "请选择一个现有 Box 作为明确的父级或前置 sibling，再确认插入。";
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

        DocumentBox? selected = SelectedBox is null
            ? null
            : _loadedBoxes.FirstOrDefault(candidate => candidate.BoxId == SelectedBox.BoxId);
        if (_loadedBoxes.Count > 0 && selected is null)
        {
            Status = "请选择插入位置：点选 logical page 作为父级，或点选 leaf 作为前置 sibling。";
            return;
        }

        IDocumentTreeEditor editor = (await _main.ServicesAsync()).DocumentTreeEditor;
        Result<DocumentBox> result;
        if (NewBoxType == DocumentBoxType.LogicalPage)
        {
            if (_loadedBoxes.Any(box => box.ParentBoxId is null && box.BoxType != DocumentBoxType.LogicalPage))
            {
                Status = "普通根 Box 不能自动转换为逻辑页；请在草稿中先显式移除或重建直属内容。";
                return;
            }

            if (_loadedBoxes.Any(box => box.BoxType == DocumentBoxType.LogicalPage) &&
                selected?.BoxType != DocumentBoxType.LogicalPage)
            {
                Status = "请选择一个 logical_page，明确新逻辑页的 sibling 插入位置。";
                return;
            }

            DocumentBoxId? afterLogical = selected?.BoxType == DocumentBoxType.LogicalPage ? selected.BoxId : null;
            result = await editor.InsertLogicalPageAsync(_editSessionId.Value, afterLogical, _pendingBBox.Value);
        }
        else
        {
            if (string.IsNullOrWhiteSpace(NewBoxText))
            {
                Status = "新建 leaf Box 必须填写有效内容。";
                return;
            }

            DocumentBoxId? parent = selected?.BoxType == DocumentBoxType.LogicalPage
                ? selected.BoxId
                : selected?.ParentBoxId;
            DocumentBoxId? after = selected?.BoxType == DocumentBoxType.LogicalPage
                ? LastSiblingForParent(selected.BoxId)?.BoxId
                : selected?.BoxId;
            result = await editor.DrawAndInsertLeafAsync(
                _editSessionId.Value,
                new InsertLeafCommand(parent, after, NewBoxType, null, null, _pendingBBox.Value,
                    CreatePayload(NewBoxType, NewBoxText),
                    NewBoxType == DocumentBoxType.Title ? NewHeadingLevel : null,
                    NewBoxType == DocumentBoxType.Code && !string.IsNullOrWhiteSpace(NewCodeLanguage)
                        ? NewCodeLanguage.Trim()
                        : null));
        }

        if (result.IsFailure)
        {
            Status = $"新增 Box 未写入草稿：{result.ErrorMessage}";
            return;
        }

        _loadedBoxes = await LoadBoxesAsync(_draftRevisionId.Value);
        PdfBBoxViewModel persisted = new(_main, this, result.Value, _widthPixels, _heightPixels, true);
        BoundingBoxes.Add(persisted);
        SelectedBox = persisted;
        ClearPendingBox();
        await RefreshPreviewAsync();
        Status = "已创建 Box；右侧文本保存到页面草稿，提交前不会影响当前版本。";
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

    private async Task SplitSelectedAsync()
    {
        if (_editSessionId is null || SelectedBox is null || SelectedBox.IsLogicalPage)
        {
            Status = "请选择一个 leaf Box 后再拆分。";
            return;
        }

        DocumentBox? original = _loadedBoxes.FirstOrDefault(box => box.BoxId == SelectedBox.BoxId);
        if (original is null)
        {
            Status = "选中的 Box 已不在当前草稿中。";
            return;
        }

        string text = SelectedBox.Text ?? string.Empty;
        string[] parts = text.Split(["\r\n\r\n", "\n\n"], StringSplitOptions.None);
        _splitSource = original;
        SplitFirstText = parts.Length > 1 ? parts[0] : text[..(text.Length / 2)];
        SplitSecondText = parts.Length > 1 ? string.Join("\n\n", parts.Skip(1)) : text[(text.Length / 2)..];
        IsDrawToolActive = true;
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
            Status = "必须先画出两个替代 bbox，并分别填写非空内容。";
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
            Status = $"拆分 Box 失败：{result.ErrorMessage}";
            return;
        }

        ClearSplit();
        await RefreshBoxesAsync();
        Status = "Box 已按两个有 bbox 的 leaf 原子拆分到页面草稿。";
    }

    private async Task RunLocalOcrAsync()
    {
        if (_currentPageId is null || string.IsNullOrWhiteSpace(Item.DocumentInstanceId) || SelectedBox is null ||
            string.IsNullOrWhiteSpace(OcrPresetId))
        {
            Status = "请先选择 leaf、填写 OCR Preset ID，并加载页面。";
            return;
        }

        if (!Guid.TryParse(OcrPresetId, out _))
        {
            Status = "OCR Preset ID 格式无效。";
            return;
        }

        DocumentBox? source = _loadedBoxes.FirstOrDefault(box => box.BoxId == SelectedBox.BoxId);
        if (source is null || source.BoxType == DocumentBoxType.LogicalPage)
        {
            Status = "局部 OCR 只能作用于 leaf Box。";
            return;
        }

        Result<OcrRegionCandidate> candidate = await (await _main.ServicesAsync()).Ocr.RecognizeRegionCandidateAsync(
            DocumentInstanceId.Parse(Item.DocumentInstanceId), Patchouli.Core.Ids.OcrPresetId.Parse(OcrPresetId),
            _currentPageId.Value, source.BBox);
        if (candidate.IsFailure)
        {
            Status = $"局部 OCR 失败：{candidate.ErrorMessage}";
            return;
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
        Status = "局部 OCR candidate 已生成；这是一个短生命周期的完整 payload diff，不会写入 OCR run 或 staging tree。";
    }

    private async Task AcceptLocalOcrAsync()
    {
        if (_editSessionId is null || _localOcrTargetBoxId is null || _localOcrCandidate is null)
        {
            Status = "请先运行局部 OCR 并选择目标 leaf。";
            return;
        }

        DocumentBox? target = _loadedBoxes.FirstOrDefault(box => box.BoxId == _localOcrTargetBoxId.Value);
        if (target is null)
        {
            Status = "candidate 与目标 Box 无法匹配。";
            return;
        }

        Result result = await (await _main.ServicesAsync()).DocumentTreeEditor.AcceptLocalOcrCandidateAsync(
            _editSessionId.Value, target.BoxId,
            new LocalOcrCandidate(_localOcrCandidate.BoxType, _localOcrCandidate.Payload,
                _localOcrCandidate.HeadingLevel));
        if (result.IsFailure)
        {
            Status = $"接受局部 OCR candidate 失败：{result.ErrorMessage}";
            return;
        }

        ClearLocalOcrCandidate();
        await RefreshBoxesAsync();
        Status = "局部 OCR candidate 已接受并写入页面草稿。";
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
        if (_currentPageId is null || string.IsNullOrWhiteSpace(Item.DocumentInstanceId) ||
            string.IsNullOrWhiteSpace(OcrPresetId) || !Guid.TryParse(OcrPresetId, out _))
        {
            Status = "请加载页面并填写有效的 OCR Preset ID。";
            return;
        }

        DocumentBox[] logicalPages = _loadedBoxes.Where(box => box.BoxType == DocumentBoxType.LogicalPage).ToArray();
        if (logicalPages.Length == 0)
        {
            Status = "当前物理页没有 logical page；请使用整页 OCR。";
            return;
        }

        AppServices services = await _main.ServicesAsync();
        Result<LogicalPageOcrResult> result = await services.LogicalPageOcr.RunAsync(
            DocumentInstanceId.Parse(Item.DocumentInstanceId), Patchouli.Core.Ids.OcrPresetId.Parse(OcrPresetId),
            _currentPageId.Value, OrderSiblings(logicalPages)
                .Select(box => new LogicalPageOcrTarget(box.BoxId, box.BBox)).ToArray());
        Status = result.IsSuccess
            ? $"logical page OCR 已合成为 staging tree {result.Value.StagingTreeRevisionId}；bbox 已映射回物理页。"
            : $"logical page OCR 失败：{result.ErrorMessage}";
    }

    private async Task RunDocumentOcrAsync()
    {
        if (string.IsNullOrWhiteSpace(Item.DocumentInstanceId) || string.IsNullOrWhiteSpace(OcrPresetId) ||
            !Guid.TryParse(OcrPresetId, out _))
        {
            Status = "请填写有效的 OCR Preset ID。";
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

        List<LogicalDocumentOcrPagePlan> plans = [];
        foreach (Page page in pages.Value)
        {
            Result<LogicalDocumentOcrPagePlan> plan = await CreatePageOcrPlanAsync(services, documentId, page.PageId);
            if (plan.IsFailure)
            {
                Status = $"读取页面 OCR 计划失败：{plan.ErrorMessage}";
                return;
            }

            plans.Add(plan.Value);
        }

        Result<LogicalDocumentOcrResult> result = await services.LogicalPageOcr.RunDocumentAsync(
            documentId, Patchouli.Core.Ids.OcrPresetId.Parse(OcrPresetId), plans);
        Status = result.IsSuccess
            ? $"文档级 OCR 已处理 {plans.Count} 个物理页并生成 {result.Value.StagingTreeRevisionIds.Count} 个 staging tree。"
            : $"文档级 OCR 失败：{result.ErrorMessage}";
    }

    private async Task RunCurrentPageOcrAsync()
    {
        if (_currentPageId is null || string.IsNullOrWhiteSpace(Item.DocumentInstanceId) ||
            string.IsNullOrWhiteSpace(OcrPresetId) || !Guid.TryParse(OcrPresetId, out _))
        {
            Status = "请加载页面并填写有效的 OCR Preset ID。";
            return;
        }

        AppServices services = await _main.ServicesAsync();
        DocumentInstanceId documentId = DocumentInstanceId.Parse(Item.DocumentInstanceId);
        Result<LogicalDocumentOcrPagePlan> plan = await CreatePageOcrPlanAsync(
            services, documentId, _currentPageId.Value);
        if (plan.IsFailure)
        {
            Status = $"读取页面 OCR 计划失败：{plan.ErrorMessage}";
            return;
        }

        Result<PhysicalPageOcrResult> result = await services.LogicalPageOcr.RunPageAsync(
            documentId, Patchouli.Core.Ids.OcrPresetId.Parse(OcrPresetId), plan.Value);
        Status = result.IsSuccess
            ? result.Value.UsedLogicalPages
                ? $"本页 OCR 已按 {result.Value.RunIds.Count} 个 logical page 生成 staging tree {result.Value.StagingTreeRevisionId}。"
                : $"本页整页 OCR 已生成 staging tree {result.Value.StagingTreeRevisionId}。"
            : $"本页 OCR 失败：{result.ErrorMessage}";
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
        SplitFirstText = string.Empty;
        SplitSecondText = string.Empty;
        Raise(nameof(IsSplitPending));
        Raise(nameof(CanConfirmSplit));
        Raise(nameof(SplitStepText));
    }

    private async Task MergeSelectedAsync()
    {
        if (_editSessionId is null || SelectedBox is null)
        {
            Status = "请选择相邻 leaf Box 后再合并。";
            return;
        }

        DocumentBox? selected = _loadedBoxes.FirstOrDefault(box => box.BoxId == SelectedBox.BoxId);
        if (selected is null)
        {
            Status = "合并需要同一 parent 下连续的 leaf Box。";
            return;
        }

        List<DocumentBox> ordered =
            OrderSiblings(_loadedBoxes.Where(box => box.ParentBoxId == selected.ParentBoxId)).ToList();
        DocumentBoxId[] selectedIds = BoundingBoxes.Where(box => box.IsMergeSelected)
            .Select(box => box.BoxId).ToArray();
        if (selectedIds.Length < 2)
        {
            DocumentBox? next = ordered.FirstOrDefault(box => box.BoxId == selected.NextSiblingBoxId);
            selectedIds = next is null ? [] : [selected.BoxId, next.BoxId];
        }

        DocumentBox[] mergeBoxes = ordered.Where(box => selectedIds.Contains(box.BoxId)).ToArray();
        int start = mergeBoxes.Length == 0 ? -1 : ordered.FindIndex(box => box.BoxId == mergeBoxes[0].BoxId);
        bool consecutive = start >= 0 && ordered.Skip(start).Take(mergeBoxes.Length)
            .Select(box => box.BoxId).SequenceEqual(mergeBoxes.Select(box => box.BoxId));
        if (mergeBoxes.Length < 2 || !consecutive ||
            mergeBoxes.Any(box => box.BoxType != selected.BoxType || box.HeadingLevel != selected.HeadingLevel))
        {
            Status = "合并需要同一 parent 下、阅读顺序连续且类型相同的 leaf。";
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
            Status = $"合并 Box 失败：{result.ErrorMessage}";
            return;
        }

        ClearMerge();
        await RefreshBoxesAsync();
        Status = "连续 Box 已合并到页面草稿。";
    }

    private void ClearMerge()
    {
        _pendingMergeBoxes = [];
        MergeText = string.Empty;
        Raise(nameof(IsMergePending));
    }

    private async Task DeleteSelectedAsync()
    {
        if (SelectedBox is null)
        {
            Status = "请先选择要删除的 Box。";
            return;
        }

        await SelectedBox.DeleteCommand.ExecuteAsync();
    }

    private async Task MoveSelectedAsync(bool down)
    {
        if (_editSessionId is null || SelectedBox is null)
        {
            Status = "请先选择要移动的 Box。";
            return;
        }

        DocumentBox? selected = _loadedBoxes.FirstOrDefault(box => box.BoxId == SelectedBox.BoxId);
        if (selected is null)
        {
            return;
        }

        List<DocumentBox> ordered =
            OrderSiblings(_loadedBoxes.Where(box => box.ParentBoxId == selected.ParentBoxId)).ToList();
        int index = ordered.FindIndex(box => box.BoxId == selected.BoxId);
        int target = index + (down ? 1 : -1);
        if (index < 0 || target < 0 || target >= ordered.Count)
        {
            Status = "Box 已在该 parent 的边界位置。";
            return;
        }

        DocumentBox? predecessor = down
            ? ordered[target]
            : target == 0
                ? null
                : ordered[target - 1];
        Result result = await (await _main.ServicesAsync()).DocumentTreeEditor.MoveBoxAsync(
            _editSessionId.Value, new MoveBoxCommand(selected.BoxId, selected.ParentBoxId, predecessor?.BoxId));
        if (result.IsFailure)
        {
            Status = $"移动 Box 失败：{result.ErrorMessage}";
            return;
        }

        await RefreshBoxesAsync();
        Status = "Box 顺序已写入页面草稿。";
    }

    internal async Task MoveBoxToAsync(PdfBBoxViewModel movingView, PdfBBoxViewModel targetView)
    {
        if (_editSessionId is null || movingView.BoxId == targetView.BoxId)
        {
            return;
        }

        DocumentBox? moving = _loadedBoxes.FirstOrDefault(box => box.BoxId == movingView.BoxId);
        DocumentBox? target = _loadedBoxes.FirstOrDefault(box => box.BoxId == targetView.BoxId);
        if (moving is null || target is null)
        {
            return;
        }

        DocumentBoxId? parent = target.BoxType == DocumentBoxType.LogicalPage ? target.BoxId : target.ParentBoxId;
        DocumentBoxId? after = target.BoxType == DocumentBoxType.LogicalPage
            ? LastSiblingForParent(target.BoxId)?.BoxId
            : target.BoxId;
        Result result = await (await _main.ServicesAsync()).DocumentTreeEditor.MoveBoxAsync(
            _editSessionId.Value, new MoveBoxCommand(moving.BoxId, parent, after));
        if (result.IsFailure)
        {
            Status = $"拖放移动失败：{result.ErrorMessage}";
            return;
        }

        await RefreshBoxesAsync();
        Status = "Box 已通过树拖放移动到页面草稿。";
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
        Raise(nameof(IsNewBoxPending));
    }

    public void RemoveBBox(PdfBBoxViewModel bbox)
    {
        BoundingBoxes.Remove(bbox);
        foreach (MarkdownPreviewBlockViewModel block in PreviewBlocks.Where(block => block.BoxId == bbox.BoxId)
                     .ToArray())
        {
            PreviewBlocks.Remove(block);
        }

        if (SelectedBox == bbox)
        {
            SelectedBox = null;
        }
    }

    public async Task LoadAsync()
    {
        _pageIndex = 0;
        _renderGeneration++;
        await RenderCurrentPageAsync();
    }

    public void Clear()
    {
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
        IsDrawToolActive = false;
        BoundingBoxes.Clear();
        PreviewBlocks.Clear();
        SelectedBox = null;
        _currentRevisionId = null;
        _draftRevisionId = null;
        _editSessionId = null;
        _loadedBoxes = [];
        ClearSplit();
        ClearPendingBox();
        ClearLocalOcrCandidate();
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
        await RenderCurrentPageAsync();
    }

    private Task ReloadAsync()
    {
        return RenderCurrentPageAsync();
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
        Status = "正在渲染 PDF 预览...";
        RaiseAll();

        try
        {
            if (string.IsNullOrWhiteSpace(Item.DocumentInstanceId) || string.IsNullOrWhiteSpace(Item.FileAssetId))
            {
                if (string.IsNullOrWhiteSpace(Item.DocumentInstanceId))
                {
                    Status = "该题录没有可预览的 PDF 文件。";
                    return;
                }
            }

            AppServices services = await _main.ServicesAsync();
            DocumentInstanceId documentInstanceId = DocumentInstanceId.Parse(Item.DocumentInstanceId);
            FileAssetId? fileAssetId = await ResolveFileAssetIdAsync(services, documentInstanceId);
            if (fileAssetId is null)
            {
                Status = "该题录没有可预览的 PDF 文件。";
                return;
            }

            Result<IReadOnlyList<Page>> pages = await services.Pages.ListPagesAsync(documentInstanceId);
            if (pages.IsFailure)
            {
                Status = $"ERROR {pages.ErrorCode}: {pages.ErrorMessage}";
                return;
            }

            _pageCount = pages.Value.Count;
            if (_pageCount == 0)
            {
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
        }
        catch (Exception ex)
        {
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

    private async Task<FileAssetId?> ResolveFileAssetIdAsync(AppServices services,
        DocumentInstanceId documentInstanceId)
    {
        if (!string.IsNullOrWhiteSpace(Item.FileAssetId))
        {
            return FileAssetId.Parse(Item.FileAssetId);
        }

        await using SqliteConnection connection = services.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        string? id = await connection.ExecuteScalarAsync<string?>(
            "select file_asset_id from document_instances where document_instance_id = @Id;",
            new { Id = documentInstanceId.ToString() });
        return string.IsNullOrWhiteSpace(id) ? null : FileAssetId.Parse(id);
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

        await LoadPreviewAsync(revisionId);
    }

    private static IEnumerable<(DocumentBox Box, int Depth)> OrderedTree(IReadOnlyList<DocumentBox> boxes)
    {
        foreach (DocumentBox root in OrderSiblings(boxes.Where(box => box.ParentBoxId is null)))
        {
            yield return (root, 0);
            if (root.BoxType != DocumentBoxType.LogicalPage)
            {
                continue;
            }

            foreach (DocumentBox child in OrderSiblings(boxes.Where(box => box.ParentBoxId == root.BoxId)))
            {
                yield return (child, 1);
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
            IsDrawToolActive = false;
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
