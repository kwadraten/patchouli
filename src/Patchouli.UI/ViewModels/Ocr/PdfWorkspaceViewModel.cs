using System.Runtime.InteropServices;
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
        DocumentBoxType.Table, DocumentBoxType.Code, DocumentBoxType.Equation, DocumentBoxType.LogicalPage
    ];

    public bool IsNewBoxPending => _pendingBBox is not null;

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

            Raise();
        }
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
    public AsyncCommand DrawToolCommand { get; }
    public AsyncCommand InsertPendingBoxCommand { get; }
    public AsyncCommand CancelPendingBoxCommand { get; }

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
                ? _loadedBoxes.LastOrDefault(box => box.ParentBoxId == selected.BoxId)?.BoxId
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

    private static DocumentBoxPayload CreatePayload(string boxType, string text)
    {
        return boxType switch
        {
            DocumentBoxType.List => new ListBoxPayload(text),
            DocumentBoxType.Table => new TableBoxPayload(text),
            DocumentBoxType.Code => new CodeBoxPayload(text),
            DocumentBoxType.Equation => new EquationBoxPayload(text),
            _ => new TextBoxPayload(text)
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
        ClearPendingBox();
        Status = "选择题录后可预览 PDF。";
        RaiseAll();
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
            Result<FileResolutionResult> resolution =
                await services.FileResolution.ResolveFileAsync(fileAssetId.Value, ResolveFilePurpose.RenderPage);
            if (resolution.IsFailure)
            {
                Status = $"ERROR {resolution.ErrorCode}: {resolution.ErrorMessage}";
                return;
            }

            if (resolution.Value.Status != FileAssetStatus.Available ||
                string.IsNullOrWhiteSpace(resolution.Value.ResolvedPath))
            {
                Status = resolution.Value.Warning ?? $"源文件不可用：{resolution.Value.Status}";
                return;
            }

            if (!string.Equals(Path.GetExtension(resolution.Value.ResolvedPath), ".pdf",
                    StringComparison.OrdinalIgnoreCase))
            {
                Status = "当前源文件不是 PDF。";
                return;
            }

            PdfPagePixelBufferOutput raster =
                await services.PdfPreviewRenderer.RenderPageToBgraBytesAsync(resolution.Value.ResolvedPath,
                    page.PageIndex, 120);
            if (generation != _renderGeneration)
            {
                return;
            }

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

    private static WriteableBitmap CreateBitmap(PdfPagePixelBufferOutput raster)
    {
        GCHandle pixels = GCHandle.Alloc(raster.BgraBytes, GCHandleType.Pinned);
        try
        {
            return new WriteableBitmap(PixelFormat.Bgra8888, AlphaFormat.Premul, pixels.AddrOfPinnedObject(),
                new PixelSize(raster.WidthPixels, raster.HeightPixels), new Vector(96, 96), raster.Stride);
        }
        finally
        {
            pixels.Free();
        }
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
        foreach (DocumentBox box in _loadedBoxes)
        {
            if (box.Suppressed && !IsEditMode)
            {
                continue;
            }

            BoundingBoxes.Add(new PdfBBoxViewModel(_main, this, box, _widthPixels, _heightPixels, isStaging));
        }

        await LoadPreviewAsync(revisionId);
    }

    internal async Task RefreshPreviewAsync()
    {
        DocumentTreeRevisionId? revisionId = IsEditMode ? _draftRevisionId : _currentRevisionId;
        if (revisionId is not null)
        {
            await LoadPreviewAsync(revisionId.Value);
        }
    }

    private async Task LoadPreviewAsync(DocumentTreeRevisionId revisionId)
    {
        PreviewBlocks.Clear();
        AppServices services = await _main.ServicesAsync();
        Result<CompiledMarkdown> compiled = await services.DocumentMarkdown.CompilePageMarkdownAsync(
            revisionId, IsEditMode);
        if (compiled.IsFailure)
        {
            return;
        }

        MarkdownDocumentModel model = services.Markdown.Parse(compiled.Value.Markdown);
        for (int index = 0; index < model.Blocks.Count; index++)
        {
            int previewIndex = index;
            MarkdownBlock block = model.Blocks[index];
            MarkdownSourceMapEntry? source = compiled.Value.SourceMap.FirstOrDefault(entry =>
                previewIndex >= entry.PreviewNodeStart &&
                previewIndex < entry.PreviewNodeStart + entry.PreviewNodeCount);
            DocumentBoxId? boxId = source?.BoxId;
            PreviewBlocks.Add(new MarkdownPreviewBlockViewModel(
                block.Kind, block.Text, block.Level, boxId,
                () =>
                {
                    SelectPreviewBox(boxId);
                    return Task.CompletedTask;
                }));
        }
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
