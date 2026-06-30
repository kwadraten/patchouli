using Avalonia.Media.Imaging;
using Dapper;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Ocr;

namespace Patchouli.UI;

public sealed class PdfPreviewViewModel : ViewModelBase
{
    private readonly MainWindowViewModel _main;
    private LibraryItemViewModel? _item;
    private int _pageIndex;
    private int _pageCount;
    private int _widthPixels;
    private int _renderGeneration;

    public PdfPreviewViewModel(MainWindowViewModel main)
    {
        _main = main;
        PreviousPageCommand = new AsyncCommand(PreviousPageAsync);
        NextPageCommand = new AsyncCommand(NextPageAsync);
        ReloadCommand = new AsyncCommand(ReloadAsync);
        ZoomInCommand = new AsyncCommand(() => { SetZoom(Zoom + 0.1); return Task.CompletedTask; });
        ZoomOutCommand = new AsyncCommand(() => { SetZoom(Zoom - 0.1); return Task.CompletedTask; });
    }

    public Bitmap? Image { get; private set; }
    public bool HasImage => Image is not null;
    public bool HasNoImage => Image is null;
    public bool IsBusy { get; private set; }
    public string Status { get; private set; } = "选择题录后可预览 PDF。";
    public string PageNumberText => _pageCount == 0 ? "-" : (_pageIndex + 1).ToString();
    public string PageTotalText => _pageCount == 0 ? "/ -" : $"/ {_pageCount}";
    public string ZoomText => $"{Math.Round(Zoom * 100):0}%";
    public double Zoom { get; private set; } = 1.0;
    public double DisplayWidth => _widthPixels <= 0 ? 620 : Math.Clamp(_widthPixels * Zoom, 240, 4000);
    public AsyncCommand PreviousPageCommand { get; }
    public AsyncCommand NextPageCommand { get; }
    public AsyncCommand ReloadCommand { get; }
    public AsyncCommand ZoomInCommand { get; }
    public AsyncCommand ZoomOutCommand { get; }

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
        _renderGeneration++;
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
        Raise(nameof(DisplayWidth));
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
            Status = $"{_item.Title} · Page {_pageIndex + 1}/{_pageCount} · {raster.WidthPixels}x{raster.HeightPixels} · {raster.RendererBasisVersion}";
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

    private void RaiseAll()
    {
        Raise(nameof(Image));
        Raise(nameof(HasImage));
        Raise(nameof(HasNoImage));
        Raise(nameof(IsBusy));
        Raise(nameof(Status));
        Raise(nameof(PageNumberText));
        Raise(nameof(PageTotalText));
        Raise(nameof(DisplayWidth));
    }
}
