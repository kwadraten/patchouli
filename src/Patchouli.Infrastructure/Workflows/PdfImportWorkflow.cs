using Patchouli.Core.Bibliography;
using Patchouli.Core.Documents;
using Patchouli.Core.Files;
using Patchouli.Core.Ids;
using Patchouli.Core.Import;
using Patchouli.Core.Layout;
using Patchouli.Core.Results;
using Patchouli.Core.Time;

namespace Patchouli.Infrastructure.Workflows;

public sealed class PdfImportWorkflow
{
    private readonly IFileAssetService _fileAssetService;
    private readonly IItemService _itemService;
    private readonly IDocumentInstanceService _documentInstanceService;
    private readonly IPageService _pageService;
    private readonly IPdfMetadataReader _pdfMetadataReader;
    private readonly IClock _clock;

    public PdfImportWorkflow(
        IFileAssetService fileAssetService,
        IItemService itemService,
        IDocumentInstanceService documentInstanceService,
        IPageService pageService,
        IPdfMetadataReader pdfMetadataReader,
        IClock clock)
    {
        _fileAssetService = fileAssetService;
        _itemService = itemService;
        _documentInstanceService = documentInstanceService;
        _pageService = pageService;
        _pdfMetadataReader = pdfMetadataReader;
        _clock = clock;
    }

    public async Task<PdfImportResult> ImportPdfAsync(
        PdfImportRequest request,
        CancellationToken cancellationToken = default)
    {
        if (!File.Exists(request.PdfPath))
            return new PdfImportResult(false, "PDF file was not found at the specified path.", null, null, null, null);

        var pageCount = request.PageCount ?? await _pdfMetadataReader.GetPageCountAsync(request.PdfPath, cancellationToken);

        if (pageCount is null or <= 0)
            return new PdfImportResult(false, "Could not determine page count for this PDF.", null, null, null, null);

        var fileAssetResult = await _fileAssetService.RegisterFileAsync(request.PdfPath, cancellationToken);
        if (fileAssetResult.IsFailure)
            return new PdfImportResult(false, fileAssetResult.ErrorMessage, null, null, null, null);

        var fileAsset = fileAssetResult.Value;

        var title = !string.IsNullOrWhiteSpace(request.Title)
            ? request.Title.Trim()
            : Path.GetFileNameWithoutExtension(request.PdfPath);

        var creatorsJson = !string.IsNullOrWhiteSpace(request.Authors)
            ? $@"[{{""name"":""{request.Authors.Trim()}""}}]"
            : null;

        var itemResult = await _itemService.CreateItemAsync(
            "document", title, creatorsJson: creatorsJson, cancellationToken: cancellationToken);

        if (itemResult.IsFailure)
            return new PdfImportResult(false, itemResult.ErrorMessage, null, null, null, null);

        var item = itemResult.Value;

        var docResult = await _documentInstanceService.AttachDocumentInstanceAsync(
            item.ItemId, fileAsset.FileAssetId, DocumentInstanceType.PrimaryScan, title: title, makePrimary: true, cancellationToken: cancellationToken);

        if (docResult.IsFailure)
            return new PdfImportResult(false, docResult.ErrorMessage, null, null, null, null);

        var documentInstance = docResult.Value;

        for (var i = 0; i < pageCount.Value; i++)
        {
            var pageResult = await _pageService.CreatePageAsync(
                documentInstance.DocumentInstanceId, i, $"Page {i + 1}",
                null, null, 0, "normalized", null, null, "import", null,
                cancellationToken: cancellationToken);

            if (pageResult.IsFailure)
                return new PdfImportResult(false, $"Failed to create page {i}: {pageResult.ErrorMessage}",
                    null, null, null, null);
        }

        return new PdfImportResult(
            true, null, "imported",
            item.ItemId.ToString(),
            fileAsset.FileAssetId.ToString(),
            documentInstance.DocumentInstanceId.ToString());
    }
}
