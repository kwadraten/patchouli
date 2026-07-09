using Avalonia.Media.Imaging;
using Dapper;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Layout;
using Patchouli.Ocr;
using System.Linq;

namespace Patchouli.UI.ViewModels;

public sealed class PdfWorkspaceViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private LibraryItemViewModel? _item;
    private int _pageIndex;
    private int _pageCount;
    private int _widthPixels;
    private int _heightPixels;
    private int _renderGeneration;
    private bool _isEditMode;
    private bool _isSidebarOpen;
    private bool _isDrawToolActive;
    private PdfBBoxViewModel? _selectedBox;
    private string? _currentRevisionId;
    private string? _draftRevisionId;
    private PageId? _currentPageId;
    private bool _isDrawing;
    private Avalonia.Point _selectionStartPoint;
    private bool _isConflictFlyoutOpen;
    private string _conflictMessage = "";

    public PdfWorkspaceViewModel(MainWindowViewModel main)
    {
        _main = main;
        PreviousPageCommand = new AsyncCommand(PreviousPageAsync);
        NextPageCommand = new AsyncCommand(NextPageAsync);
        ReloadCommand = new AsyncCommand(ReloadAsync);
        ZoomInCommand = new AsyncCommand(() => { SetZoom(Zoom + 0.1); return Task.CompletedTask; });
        ZoomOutCommand = new AsyncCommand(() => { SetZoom(Zoom - 0.1); return Task.CompletedTask; });
        
        EnterEditModeCommand = new AsyncCommand(EnterEditModeAsync);
        SaveAndExitCommand = new AsyncCommand(SaveAndExitAsync);
        CancelEditModeCommand = new AsyncCommand(CancelEditModeAsync);
        SelectToolCommand = new AsyncCommand(() => { IsDrawToolActive = false; return Task.CompletedTask; });
        DrawToolCommand = new AsyncCommand(() => { IsDrawToolActive = true; return Task.CompletedTask; });
        ResolveConflictAdjustCommand = new AsyncCommand(ResolveConflictAdjustAsync);
        ResolveConflictOverwriteCommand = new AsyncCommand(ResolveConflictOverwriteAsync);
        ResolveConflictSkipCommand = new AsyncCommand(ResolveConflictSkipAsync);
    }

    public Bitmap? Image { get; private set; }
    public bool HasImage => Image is not null;
    public bool HasNoImage => Image is null;
    public bool IsBusy { get; private set; }
    private string _status = "选择题录后可预览 PDF。";
    public string Status
    {
        get => _status;
        set
        {
            if (_status == value) return;
            _status = value;
            Raise();
            if (!string.IsNullOrWhiteSpace(value))
            {
                if (value.Contains("失败", StringComparison.Ordinal) || value.Contains("不可用", StringComparison.Ordinal) || value.StartsWith("ERROR", StringComparison.Ordinal)) _main.ReportError(value);
                else _main.Report(value);
            }
        }
    }
    public string PageNumberText => _pageCount == 0 ? "-" : (_pageIndex + 1).ToString();
    public string PageTotalText => _pageCount == 0 ? "/ -" : $"/ {_pageCount}";
    public string ZoomText => $"{Math.Round(Zoom * 100):0}%";
    public double Zoom { get; private set; } = 1.0;
    public double ActualWidthPixels => _widthPixels;
    public double ActualHeightPixels => _heightPixels;

    public bool IsEditMode { get => _isEditMode; private set { if (_isEditMode == value) return; _isEditMode = value; Raise(); } }
    public bool IsSidebarOpen { get => _isSidebarOpen; set { if (_isSidebarOpen == value) return; _isSidebarOpen = value; Raise(); Raise(nameof(SidebarMaxWidth)); Raise(nameof(SidebarMinWidth)); } }
    public double SidebarMaxWidth => _isSidebarOpen ? 800.0 : 0.0;
    public double SidebarMinWidth => _isSidebarOpen ? 200.0 : 0.0;
    private double _selectionLeft;
    private double _selectionTop;
    private double _selectionWidth;
    private double _selectionHeight;

    public bool IsDrawToolActive { get => _isDrawToolActive; private set { if (_isDrawToolActive == value) return; _isDrawToolActive = value; Raise(); } }
    
    public bool IsDrawing { get => _isDrawing; private set { if (_isDrawing == value) return; _isDrawing = value; Raise(); Raise(nameof(SelectionVisible)); } }
    
    public double SelectionLeft { get => _selectionLeft; private set { if (_selectionLeft == value) return; _selectionLeft = value; Raise(); } }
    public double SelectionTop { get => _selectionTop; private set { if (_selectionTop == value) return; _selectionTop = value; Raise(); } }
    public double SelectionWidth { get => _selectionWidth; private set { if (_selectionWidth == value) return; _selectionWidth = value; Raise(); Raise(nameof(SelectionVisible)); } }
    public double SelectionHeight { get => _selectionHeight; private set { if (_selectionHeight == value) return; _selectionHeight = value; Raise(); Raise(nameof(SelectionVisible)); } }
    
    public bool SelectionVisible => IsDrawing && SelectionWidth > 0 && SelectionHeight > 0;
    
    public bool IsConflictFlyoutOpen { get => _isConflictFlyoutOpen; set { if (_isConflictFlyoutOpen == value) return; _isConflictFlyoutOpen = value; Raise(); } }
    public string ConflictMessage { get => _conflictMessage; set { if (_conflictMessage == value) return; _conflictMessage = value; Raise(); } }
    public AsyncCommand ResolveConflictAdjustCommand { get; }
    public AsyncCommand ResolveConflictOverwriteCommand { get; }
    public AsyncCommand ResolveConflictSkipCommand { get; }

    public System.Collections.ObjectModel.ObservableCollection<PdfBBoxViewModel> BoundingBoxes { get; } = new();
    
    public PdfBBoxViewModel? SelectedBox
    {
        get => _selectedBox;
        set
        {
            if (_selectedBox == value) return;
            if (_selectedBox != null) _selectedBox.IsSelected = false;
            _selectedBox = value;
            if (_selectedBox != null) _selectedBox.IsSelected = true;
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

    public void OnPointerPressed(double x, double y)
    {
        if (!IsEditMode || !IsDrawToolActive) return;
        _selectionStartPoint = new Avalonia.Point(x, y);
        SelectionLeft = x;
        SelectionTop = y;
        SelectionWidth = 0;
        SelectionHeight = 0;
        IsDrawing = true;
    }

    public void OnPointerMoved(double x, double y)
    {
        if (!IsDrawing) return;
        var currentPoint = new Avalonia.Point(x, y);
        SelectionLeft = Math.Min(_selectionStartPoint.X, currentPoint.X);
        SelectionTop = Math.Min(_selectionStartPoint.Y, currentPoint.Y);
        SelectionWidth = Math.Max(_selectionStartPoint.X, currentPoint.X) - SelectionLeft;
        SelectionHeight = Math.Max(_selectionStartPoint.Y, currentPoint.Y) - SelectionTop;
    }

    public void OnPointerReleased()
    {
        if (!IsDrawing) return;
        IsDrawing = false;
        if (SelectionWidth > 5 && SelectionHeight > 5)
        {
            _ = CreateBBoxFromSelectionAsync();
        }
        SelectionWidth = 0;
        SelectionHeight = 0;
    }

    private PdfBBoxViewModel? _pendingStagingBox;

    private async Task CreateBBoxFromSelectionAsync()
    {
        if (_draftRevisionId is null || _currentPageId is null || _widthPixels <= 0 || _heightPixels <= 0)
        {
            Status = "请先进入编辑模式并加载页面。";
            return;
        }

        var box = new PdfBBoxViewModel(_main, this, LayoutNodeId.New(), SelectionLeft, SelectionTop, SelectionWidth, SelectionHeight, LayoutNodeType.Paragraph, Avalonia.Media.Brushes.DarkOrange)
        {
            IsStaging = true
        };
        
        if (CheckOverlap(box))
        {
            _pendingStagingBox = box;
            ConflictMessage = "边框发生重叠 (CF-06)。请选择处理方式：";
            IsConflictFlyoutOpen = true;
        }
        else
        {
            await PersistStagingBoxAsync(box);
        }
    }

    private async Task ResolveConflictAdjustAsync()
    {
        IsConflictFlyoutOpen = false;
        if (_pendingStagingBox != null)
        {
            // Just add it but let the user adjust it
            await PersistStagingBoxAsync(_pendingStagingBox);
        }
        _pendingStagingBox = null;
    }

    private async Task ResolveConflictOverwriteAsync()
    {
        IsConflictFlyoutOpen = false;
        if (_pendingStagingBox != null)
        {
            // Remove any overlapping existing boxes
            var rect1 = new Avalonia.Rect(_pendingStagingBox.Left, _pendingStagingBox.Top, _pendingStagingBox.Width, _pendingStagingBox.Height);
            var toRemove = BoundingBoxes.Where(existing => 
            {
                var rect2 = new Avalonia.Rect(existing.Left, existing.Top, existing.Width, existing.Height);
                return rect1.Intersects(rect2);
            }).ToList();
            
            foreach (var box in toRemove)
            {
                BoundingBoxes.Remove(box);
            }
            
            await PersistStagingBoxAsync(_pendingStagingBox);
        }
        _pendingStagingBox = null;
    }

    private Task ResolveConflictSkipAsync()
    {
        IsConflictFlyoutOpen = false;
        _pendingStagingBox = null;
        return Task.CompletedTask;
    }

    private bool CheckOverlap(PdfBBoxViewModel newBox)
    {
        var rect1 = new Avalonia.Rect(newBox.Left, newBox.Top, newBox.Width, newBox.Height);
        foreach (var existing in BoundingBoxes)
        {
            var rect2 = new Avalonia.Rect(existing.Left, existing.Top, existing.Width, existing.Height);
            if (rect1.Intersects(rect2))
            {
                return true;
            }
        }
        return false;
    }

    private async Task PersistStagingBoxAsync(PdfBBoxViewModel box)
    {
        if (_draftRevisionId is null || _currentPageId is null) return;

        var x = Math.Clamp(box.Left / _widthPixels, 0, 1);
        var y = Math.Clamp(box.Top / _heightPixels, 0, 1);
        var width = Math.Clamp(box.Width / _widthPixels, 0.0001, 1 - x);
        var height = Math.Clamp(box.Height / _heightPixels, 0.0001, 1 - y);
        var bbox = new NormalizedBBox(x, y, width, height);
        var result = await (await _main.ServicesAsync()).Layout.AddNodeAsync(
            LayoutRevisionId.Parse(_draftRevisionId),
            _currentPageId.Value,
            null,
            LayoutNodeType.Paragraph,
            bbox,
            box.Text,
            TextPolicy.Own,
            BoundingBoxes.Count + 1,
            LayoutNodeSource.Manual);
        if (result.IsFailure)
        {
            Status = $"新增 bbox 未写入 layout：{result.ErrorMessage}";
            return;
        }

        var persisted = new PdfBBoxViewModel(_main, this, result.Value, _widthPixels, _heightPixels)
        {
            IsStaging = true
        };
        BoundingBoxes.Add(persisted);
        SelectedBox = persisted;
        Status = "已创建局部 OCR 候选 bbox，可在右侧编辑文本并保存草稿。";
    }

    public void RemoveBBox(PdfBBoxViewModel bbox)
    {
        BoundingBoxes.Remove(bbox);
        if (SelectedBox == bbox) SelectedBox = null;
    }

    public async Task LoadSelectedItemAsync(LibraryItemViewModel? item)
    {
        _item = item;
        _pageIndex = 0;
        _renderGeneration++;
        await RenderCurrentPageAsync();
    }

    public void Clear()
    {
        Image?.Dispose();
        Image = null;
        _item = null;
        _pageIndex = 0;
        _pageCount = 0;
        _widthPixels = 0;
        _heightPixels = 0;
        _renderGeneration++;
        IsEditMode = false;
        IsSidebarOpen = false;
        IsDrawToolActive = false;
        BoundingBoxes.Clear();
        SelectedBox = null;
        _currentRevisionId = null;
        _draftRevisionId = null;
        Status = "选择题录后可预览 PDF。";
        RaiseAll();
    }

    private async Task PreviousPageAsync()
    {
        if (_pageIndex <= 0) return;
        _pageIndex--;
        await RenderCurrentPageAsync();
    }

    private async Task NextPageAsync()
    {
        if (_pageCount > 0 && _pageIndex >= _pageCount - 1) return;
        _pageIndex++;
        await RenderCurrentPageAsync();
    }

    private Task ReloadAsync() => RenderCurrentPageAsync();

    private void SetZoom(double value)
    {
        Zoom = Math.Clamp(value, 0.25, 4.0);
        Raise(nameof(Zoom));
        Raise(nameof(ZoomText));
    }

    private async Task RenderCurrentPageAsync()
    {
        var generation = ++_renderGeneration;
        Image?.Dispose();
        Image = null;
        _widthPixels = 0;
        IsBusy = true;
        Status = "正在渲染 PDF 预览...";
        RaiseAll();

        try
        {
            if (_item is null)
            {
                Status = "未选择题录。";
                return;
            }

            if (string.IsNullOrWhiteSpace(_item.DocumentInstanceId) || string.IsNullOrWhiteSpace(_item.FileAssetId))
            {
                if (string.IsNullOrWhiteSpace(_item.DocumentInstanceId))
                {
                    Status = "该题录没有可预览的 PDF 文件。";
                    return;
                }
            }

            var services = await _main.ServicesAsync();
            var documentInstanceId = DocumentInstanceId.Parse(_item.DocumentInstanceId);
            var fileAssetId = await ResolveFileAssetIdAsync(services, documentInstanceId);
            if (fileAssetId is null)
            {
                Status = "该题录没有可预览的 PDF 文件。";
                return;
            }

            var pages = await services.Pages.ListPagesAsync(documentInstanceId);
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
            var page = pages.Value[_pageIndex];
            _currentPageId = page.PageId;
            var resolution = await services.FileResolution.ResolveFileAsync(fileAssetId.Value, ResolveFilePurpose.RenderPage);
            if (resolution.IsFailure)
            {
                Status = $"ERROR {resolution.ErrorCode}: {resolution.ErrorMessage}";
                return;
            }

            if (resolution.Value.Status != FileAssetStatus.Available || string.IsNullOrWhiteSpace(resolution.Value.ResolvedPath))
            {
                Status = resolution.Value.Warning ?? $"源文件不可用：{resolution.Value.Status}";
                return;
            }

            if (!string.Equals(Path.GetExtension(resolution.Value.ResolvedPath), ".pdf", StringComparison.OrdinalIgnoreCase))
            {
                Status = "当前源文件不是 PDF。";
                return;
            }

            var raster = await services.PdfPreviewRenderer.RenderPageToPngBytesAsync(resolution.Value.ResolvedPath, page.PageIndex, 120);
            if (generation != _renderGeneration) return;
            await using var stream = new MemoryStream(raster.PngBytes);
            Image = new Bitmap(stream);
            _widthPixels = raster.WidthPixels;
            _heightPixels = raster.HeightPixels;
            
            BoundingBoxes.Clear();
            SelectedBox = null;
            if (_isEditMode && _draftRevisionId != null)
            {
                await LoadNodesAsync(page.PageId, LayoutRevisionId.Parse(_draftRevisionId));
            }
            else
            {
                var rev = await services.Layout.GetCurrentRevisionAsync(documentInstanceId);
                if (rev.IsSuccess)
                {
                    _currentRevisionId = rev.Value.LayoutRevisionId.ToString();
                    await LoadNodesAsync(page.PageId, rev.Value.LayoutRevisionId);
                }
            }

            Status = $"{_item.Title} · 第 {_pageIndex + 1}/{_pageCount} 页 · {raster.WidthPixels}x{raster.HeightPixels} · {raster.RendererBasisVersion}";
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

    private async Task<FileAssetId?> ResolveFileAssetIdAsync(AppServices services, DocumentInstanceId documentInstanceId)
    {
        if (!string.IsNullOrWhiteSpace(_item?.FileAssetId))
            return FileAssetId.Parse(_item.FileAssetId);

        await using var connection = services.ConnectionFactory.CreateConnection();
        await connection.OpenAsync();
        var id = await connection.ExecuteScalarAsync<string?>(
            "select file_asset_id from document_instances where document_instance_id = @Id;",
            new { Id = documentInstanceId.ToString() });
        return string.IsNullOrWhiteSpace(id) ? null : FileAssetId.Parse(id);
    }

    private async Task LoadNodesAsync(PageId pageId, LayoutRevisionId revisionId)
    {
        var services = await _main.ServicesAsync();
        var nodesResult = await services.Layout.ListNodesForPageAsync(pageId, revisionId);
        if (nodesResult.IsSuccess)
        {
            foreach (var node in nodesResult.Value)
            {
                if (node.Ignored && !_isEditMode) continue;
                BoundingBoxes.Add(new PdfBBoxViewModel(_main, this, node, _widthPixels, _heightPixels));
            }
        }
    }

    private async Task EnterEditModeAsync()
    {
        if (_item is null || string.IsNullOrWhiteSpace(_item.DocumentInstanceId))
        {
            Status = "该题录没有可编辑的文档实例。";
            return;
        }

        var services = await _main.ServicesAsync();
        var docId = DocumentInstanceId.Parse(_item.DocumentInstanceId);
        var rev = await services.Layout.CreateLayoutRevisionAsync(docId, Patchouli.Core.Layout.LayoutRevisionSource.Manual, false);
        if (rev.IsSuccess)
        {
            _draftRevisionId = rev.Value.LayoutRevisionId.ToString();
            IsEditMode = true;
            IsSidebarOpen = true;
            IsDrawToolActive = false;
            await ReloadAsync();
        }
        else
        {
            Status = $"无法进入编辑模式：{rev.ErrorMessage}";
        }
    }

    private async Task SaveAndExitAsync()
    {
        if (_item is null || string.IsNullOrWhiteSpace(_item.DocumentInstanceId) || _draftRevisionId is null)
        {
            return;
        }

        var services = await _main.ServicesAsync();
        var docId = DocumentInstanceId.Parse(_item.DocumentInstanceId);
        var res = await services.Layout.SetCurrentRevisionAsync(docId, LayoutRevisionId.Parse(_draftRevisionId));
        if (res.IsSuccess)
        {
            IsEditMode = false;
            _draftRevisionId = null;
            await ReloadAsync();
        }
        else
        {
            Status = $"保存失败：{res.ErrorMessage}";
        }
    }

    private async Task CancelEditModeAsync()
    {
        IsEditMode = false;
        _draftRevisionId = null;
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
