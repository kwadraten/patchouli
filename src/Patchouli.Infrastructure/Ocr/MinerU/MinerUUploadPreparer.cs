using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Hashing;
using Patchouli.Ocr;
using Patchouli.Ocr.MinerU;
using FileInfo = System.IO.FileInfo;

namespace Patchouli.Infrastructure.Ocr.MinerU;

public sealed record MinerUUploadLimits(int MaxPagesPerFile, long MaxBytesPerFile, long TargetBytesPerFile)
{
    public const int OfficialMaxPagesPerFile = 200;
    public const long OfficialMaxBytesPerFile = 200L * 1024 * 1024;

    public static MinerUUploadLimits Default { get; } =
        new(OfficialMaxPagesPerFile, OfficialMaxBytesPerFile, 190L * 1024 * 1024);

    public MinerUUploadLimits(int maxPagesPerFile, long maxBytesPerFile)
        : this(maxPagesPerFile, maxBytesPerFile, Math.Max(1, Math.Min(maxBytesPerFile, (long)(maxBytesPerFile * 0.95))))
    {
    }
}

public sealed record MinerUPdfChunk(int StartPageIndex, int PageCount, long EstimatedBytes)
{
    public int EndPageIndex => StartPageIndex + PageCount - 1;
    public string PageRange => $"{StartPageIndex + 1}-{EndPageIndex + 1}";
}

public static class MinerUPdfChunkPlanner
{
    public static IReadOnlyList<MinerUPdfChunk> PlanChunks(int pageCount, long sourceSizeBytes,
        MinerUUploadLimits? limits = null)
    {
        if (pageCount <= 0)
        {
            return [];
        }

        limits ??= MinerUUploadLimits.Default;
        long estimatedBytesPerPage = Math.Max(1, (long)Math.Ceiling(sourceSizeBytes / (double)pageCount));
        int pagesBySize = Math.Max(1, (int)Math.Floor(limits.TargetBytesPerFile / (double)estimatedBytesPerPage));
        int maxPages = Math.Max(1, Math.Min(limits.MaxPagesPerFile, pagesBySize));
        List<MinerUPdfChunk> chunks = new();
        for (int start = 0; start < pageCount; start += maxPages)
        {
            int count = Math.Min(maxPages, pageCount - start);
            chunks.Add(new MinerUPdfChunk(start, count, estimatedBytesPerPage * count));
        }

        return chunks;
    }
}

internal sealed record MinerUPreparedResult(
    string ZipPath,
    IReadOnlyList<int> PageIndexMap,
    OcrImageContext? ImageContext);

public sealed class MinerUUploadPreparer
{
    private readonly IMinerUClient _client;
    private readonly MinerUUploadLimits _limits;
    private readonly PdfiumDocumentEngine _pdfium;

    public MinerUUploadPreparer(IMinerUClient client, MinerUUploadLimits? limits = null,
        PdfiumDocumentEngine? pdfium = null)
    {
        _client = client;
        _limits = limits ?? MinerUUploadLimits.Default;
        _pdfium = pdfium ?? new PdfiumDocumentEngine();
    }

    internal async Task<Result<MinerUPreparedResult>> PrepareAndUploadAsync(
        OcrUploadSource source,
        string downloadDirectory,
        CancellationToken cancellationToken = default)
    {
        return source switch
        {
            OcrUploadSource.WholeDocument wholeDocument => ToPrepared(
                await UploadDocumentAsync(wholeDocument.PdfPath, downloadDirectory, cancellationToken), null),
            OcrUploadSource.PageRanges pageRanges => await PreparePageRangesAsync(
                pageRanges, downloadDirectory, cancellationToken),
            OcrUploadSource.PageImage pageImage => await PrepareImageAsync(
                pageImage.ImagePath, pageImage.Context, downloadDirectory, cancellationToken),
            OcrUploadSource.RegionImage regionImage => await PrepareImageAsync(
                regionImage.ImagePath, regionImage.Context, downloadDirectory, cancellationToken),
            _ => Result<MinerUPreparedResult>.Failure("validation_failed", "Unknown OCR upload source.")
        };
    }

    public async Task<Result<MinerUDownloadedResult>> UploadAndExtractAsync(
        string pdfPath,
        string downloadDirectory,
        CancellationToken cancellationToken = default)
    {
        return await UploadDocumentAsync(pdfPath, downloadDirectory, cancellationToken);
    }

    public async Task<Result<string>> UploadAndExtractImageAsync(
        string imagePath,
        string downloadDirectory,
        CancellationToken cancellationToken = default)
    {
        Result<MinerUPreparedResult> prepared = await PrepareImageAsync(
            imagePath, new OcrImageContext(0, 0, 0, null), downloadDirectory, cancellationToken);
        return prepared.IsFailure
            ? Result<string>.Failure(prepared.ErrorCode!, prepared.ErrorMessage!)
            : new MinerUResultParser().ParsePlainText(prepared.Value);
    }

    private async Task<Result<MinerUDownloadedResult>> UploadDocumentAsync(
        string pdfPath,
        string downloadDirectory,
        CancellationToken cancellationToken)
    {
        FileInfo fileInfo = new(pdfPath);
        if (!fileInfo.Exists)
        {
            return Result<MinerUDownloadedResult>.Failure("file_not_found", "PDF file was not found.");
        }

        string dataId = await Blake3Hash.ComputeFileAsync(pdfPath, cancellationToken);
        int? pageCount = await GetPageCountAsync(pdfPath, cancellationToken);
        if (pageCount is null)
        {
            if (fileInfo.Length > _limits.MaxBytesPerFile)
            {
                return Result<MinerUDownloadedResult>.Failure("pdf_page_count_unavailable",
                    "PDF exceeds MinerU's file-size limit and page count could not be read for safe splitting.");
            }

            MinerUUploadRequest uploadRequest = new(pdfPath, fileInfo.Name, fileInfo.Length, dataId);
            return await UploadSingleAsync(uploadRequest, downloadDirectory, cancellationToken);
        }

        IReadOnlyList<MinerUPdfChunk> chunks =
            MinerUPdfChunkPlanner.PlanChunks(pageCount.Value, fileInfo.Length, _limits);
        if (chunks.Count <= 1 && fileInfo.Length <= _limits.MaxBytesPerFile && pageCount <= _limits.MaxPagesPerFile)
        {
            MinerUUploadRequest uploadRequest = new(pdfPath, fileInfo.Name, fileInfo.Length, dataId);
            return await UploadSingleAsync(uploadRequest, downloadDirectory, cancellationToken);
        }

        string chunkDirectory = Path.Combine(downloadDirectory, "mineru-upload-chunks",
            Path.GetFileNameWithoutExtension(pdfPath) + "-" + dataId[..12]);
        Result<IReadOnlyList<ChunkUploadFile>> chunkFiles = await CreateChunkFilesAsync(pdfPath, fileInfo.Name, dataId,
            chunks, chunkDirectory, cancellationToken);
        if (chunkFiles.IsFailure)
        {
            return Result<MinerUDownloadedResult>.Failure(chunkFiles.ErrorCode!, chunkFiles.ErrorMessage!);
        }

        List<(int PageIndexOffset, MinerUDownloadedResult Download)> downloads = new();
        foreach (ChunkUploadFile chunkFile in chunkFiles.Value)
        {
            Result<MinerUDownloadedResult> result = await UploadSingleAsync(chunkFile.Request,
                Path.Combine(downloadDirectory, "mineru-results"), cancellationToken);
            if (result.IsFailure)
            {
                return Result<MinerUDownloadedResult>.Failure(result.ErrorCode!, result.ErrorMessage!);
            }

            downloads.Add((chunkFile.Chunk.StartPageIndex, result.Value));
        }

        if (downloads.Count == 1 && downloads[0].PageIndexOffset == 0)
        {
            return Result<MinerUDownloadedResult>.Success(downloads[0].Download);
        }

        Result<string> merged = MergeResultZips(downloads, downloadDirectory);
        return merged.IsSuccess
            ? Result<MinerUDownloadedResult>.Success(new MinerUDownloadedResult("merged-" + dataId[..12], merged.Value,
                MinerUProviderStatus.Done))
            : Result<MinerUDownloadedResult>.Failure(merged.ErrorCode!, merged.ErrorMessage!);
    }

    private async Task<Result<MinerUPreparedResult>> PreparePageRangesAsync(
        OcrUploadSource.PageRanges source,
        string downloadDirectory,
        CancellationToken cancellationToken)
    {
        if (source.Ranges.Count == 0 ||
            source.Ranges.Any(range => range.StartPageIndex < 0 || range.PageCount <= 0))
        {
            return Result<MinerUPreparedResult>.Failure("validation_failed",
                "MinerU page-range uploads require non-empty, valid page ranges.");
        }

        FileInfo fileInfo = new(source.PdfPath);
        if (!fileInfo.Exists)
        {
            return Result<MinerUPreparedResult>.Failure("file_not_found", "PDF file was not found.");
        }

        string dataId = await Blake3Hash.ComputeFileAsync(source.PdfPath, cancellationToken);
        string rangesDirectory = Path.Combine(downloadDirectory, "mineru-upload-chunks",
            Path.GetFileNameWithoutExtension(source.PdfPath) + "-" + dataId[..12]);
        try
        {
            Directory.CreateDirectory(rangesDirectory);
            List<(int PageIndexOffset, MinerUDownloadedResult Download)> downloads = new();
            foreach (OcrPageRange range in source.Ranges)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string subPdfPath = Path.Combine(rangesDirectory, BuildRangeFileName(fileInfo.Name, range));
                await _pdfium.ExtractPagesAsync(source.PdfPath, subPdfPath, range.StartPageIndex, range.PageCount,
                    cancellationToken);
                FileInfo subPdfInfo = new(subPdfPath);
                IReadOnlyList<MinerUPdfChunk> chunks =
                    MinerUPdfChunkPlanner.PlanChunks(range.PageCount, subPdfInfo.Length, _limits);
                if (chunks.Count <= 1 && subPdfInfo.Length <= _limits.MaxBytesPerFile &&
                    range.PageCount <= _limits.MaxPagesPerFile)
                {
                    string subDataId =
                        $"{dataId[..Math.Min(32, dataId.Length)]}-{range.StartPageIndex + 1}-{range.StartPageIndex + range.PageCount}";
                    MinerUUploadRequest uploadRequest = new(subPdfPath, Path.GetFileName(subPdfPath),
                        subPdfInfo.Length, subDataId);
                    Result<MinerUDownloadedResult> uploaded = await UploadSingleAsync(uploadRequest,
                        Path.Combine(downloadDirectory, "mineru-results"), cancellationToken);
                    if (uploaded.IsFailure)
                    {
                        return Result<MinerUPreparedResult>.Failure(uploaded.ErrorCode!, uploaded.ErrorMessage!);
                    }

                    downloads.Add((range.StartPageIndex, uploaded.Value));
                    continue;
                }

                Result<IReadOnlyList<ChunkUploadFile>> chunkFiles = await CreateChunkFilesAsync(subPdfPath,
                    Path.GetFileName(subPdfPath), dataId, chunks, rangesDirectory, cancellationToken);
                if (chunkFiles.IsFailure)
                {
                    return Result<MinerUPreparedResult>.Failure(chunkFiles.ErrorCode!, chunkFiles.ErrorMessage!);
                }

                foreach (ChunkUploadFile chunkFile in chunkFiles.Value)
                {
                    Result<MinerUDownloadedResult> uploaded = await UploadSingleAsync(chunkFile.Request,
                        Path.Combine(downloadDirectory, "mineru-results"), cancellationToken);
                    if (uploaded.IsFailure)
                    {
                        return Result<MinerUPreparedResult>.Failure(uploaded.ErrorCode!, uploaded.ErrorMessage!);
                    }

                    downloads.Add((range.StartPageIndex + chunkFile.Chunk.StartPageIndex, uploaded.Value));
                }
            }

            if (downloads.Count == 1 && downloads[0].PageIndexOffset == 0)
            {
                return ToPrepared(Result<MinerUDownloadedResult>.Success(downloads[0].Download), null);
            }

            Result<string> merged = MergeResultZips(downloads, downloadDirectory);
            return ToPrepared(merged.IsSuccess
                ? Result<MinerUDownloadedResult>.Success(new MinerUDownloadedResult(
                    "merged-" + dataId[..12], merged.Value, MinerUProviderStatus.Done))
                : Result<MinerUDownloadedResult>.Failure(merged.ErrorCode!, merged.ErrorMessage!), null);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                       "infrastructure.mineru-upload-preparer"))
        {
            return Result<MinerUPreparedResult>.Failure("pdf_split_failed",
                $"Failed to split PDF for MinerU upload: {ex.Message}");
        }
    }

    private async Task<Result<MinerUPreparedResult>> PrepareImageAsync(
        string imagePath,
        OcrImageContext context,
        string downloadDirectory,
        CancellationToken cancellationToken)
    {
        FileInfo fileInfo = new(imagePath);
        if (!fileInfo.Exists)
        {
            return Result<MinerUPreparedResult>.Failure("file_not_found", "Image file was not found.");
        }

        string dataId = await Blake3Hash.ComputeFileAsync(imagePath, cancellationToken);
        string fileName = fileInfo.Name.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
            ? fileInfo.Name
            : Path.GetFileNameWithoutExtension(fileInfo.Name) + ".png";
        MinerUUploadRequest uploadRequest = new(imagePath, fileName, fileInfo.Length, dataId);
        return ToPrepared(await UploadSingleAsync(uploadRequest, downloadDirectory, cancellationToken), context);
    }

    private static Result<MinerUPreparedResult> ToPrepared(
        Result<MinerUDownloadedResult> download,
        OcrImageContext? imageContext)
    {
        if (download.IsFailure)
        {
            return Result<MinerUPreparedResult>.Failure(download.ErrorCode!, download.ErrorMessage!);
        }

        try
        {
            using MinerUZipReader reader = MinerUZipReader.Open(download.Value.ZipPath);
            string? contentListJson = reader.ReadFileContent("_content_list_v2.json")
                                      ?? reader.ReadFileContent("_content_list.json");
            MinerUContentListDocument? document = contentListJson is null
                ? null
                : new MinerUContentListParser().Parse(contentListJson);
            IReadOnlyList<int> pageIndexMap = document is null
                ? []
                : document.Pages.Select(page => page.PageNum - 1).Distinct().OrderBy(index => index).ToArray();
            return Result<MinerUPreparedResult>.Success(
                new MinerUPreparedResult(download.Value.ZipPath, pageIndexMap, imageContext));
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                       "infrastructure.mineru-upload-preparer"))
        {
            return Result<MinerUPreparedResult>.Failure("zip_read_error",
                $"Failed to read result zip: {ex.Message}");
        }
    }

    private async Task<Result<MinerUDownloadedResult>> UploadSingleAsync(
        MinerUUploadRequest uploadRequest,
        string downloadDirectory,
        CancellationToken cancellationToken)
    {
        Result<MinerUUploadBatch> urlResult = await _client.RequestUploadUrlsAsync([uploadRequest], cancellationToken);
        if (urlResult.IsFailure)
        {
            return Result<MinerUDownloadedResult>.Failure(urlResult.ErrorCode!, urlResult.ErrorMessage!);
        }

        MinerUUploadBatch batch = urlResult.Value;
        MinerUFileUploadUrl fileUrl = batch.FileUrls[0];

        Result uploadResult =
            await _client.UploadFileAsync(fileUrl.UploadUrl, uploadRequest.LocalPath, cancellationToken);
        if (uploadResult.IsFailure)
        {
            return Result<MinerUDownloadedResult>.Failure(uploadResult.ErrorCode!, uploadResult.ErrorMessage!);
        }

        return await _client.WaitForCompletionAndDownloadAsync(batch.BatchId, downloadDirectory, cancellationToken);
    }

    private async Task<int?> GetPageCountAsync(string pdfPath, CancellationToken cancellationToken)
    {
        try
        {
            int pageCount = await _pdfium.GetPageCountAsync(pdfPath, cancellationToken);
            return pageCount > 0 ? pageCount : null;
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch
        {
            return null;
        }
    }

    private async Task<Result<IReadOnlyList<ChunkUploadFile>>> CreateChunkFilesAsync(
        string pdfPath,
        string originalFileName,
        string sourceDataId,
        IReadOnlyList<MinerUPdfChunk> plannedChunks,
        string chunkDirectory,
        CancellationToken cancellationToken)
    {
        try
        {
            Directory.CreateDirectory(chunkDirectory);
            List<ChunkUploadFile> uploadFiles = new();
            foreach (MinerUPdfChunk chunk in plannedChunks)
            {
                await WriteChunkRecursiveAsync(pdfPath, originalFileName, sourceDataId, chunk, chunkDirectory,
                    uploadFiles, cancellationToken);
            }

            return Result<IReadOnlyList<ChunkUploadFile>>.Success(uploadFiles);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                       "infrastructure.mineru-upload-preparer"))
        {
            return Result<IReadOnlyList<ChunkUploadFile>>.Failure("pdf_split_failed",
                $"Failed to split PDF for MinerU upload: {ex.Message}");
        }
    }

    private async Task WriteChunkRecursiveAsync(
        string sourcePath,
        string originalFileName,
        string sourceDataId,
        MinerUPdfChunk chunk,
        string chunkDirectory,
        List<ChunkUploadFile> uploadFiles,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        string path = Path.Combine(chunkDirectory, BuildChunkFileName(originalFileName, chunk));
        await _pdfium.ExtractPagesAsync(sourcePath, path, chunk.StartPageIndex, chunk.PageCount, cancellationToken);

        long size = new FileInfo(path).Length;
        if (size > _limits.MaxBytesPerFile)
        {
            if (chunk.PageCount <= 1)
            {
                throw new InvalidOperationException(
                    $"A single PDF page exceeds MinerU's {MinerUUploadLimits.OfficialMaxBytesPerFile / (1024 * 1024)}MB upload limit.");
            }

            File.Delete(path);
            int leftCount = chunk.PageCount / 2;
            int rightCount = chunk.PageCount - leftCount;
            await WriteChunkRecursiveAsync(sourcePath, originalFileName, sourceDataId,
                chunk with { PageCount = leftCount },
                chunkDirectory, uploadFiles, cancellationToken);
            await WriteChunkRecursiveAsync(sourcePath, originalFileName, sourceDataId,
                new MinerUPdfChunk(chunk.StartPageIndex + leftCount, rightCount, chunk.EstimatedBytes / 2),
                chunkDirectory, uploadFiles, cancellationToken);
            return;
        }

        string dataId =
            $"{sourceDataId[..Math.Min(32, sourceDataId.Length)]}-{chunk.StartPageIndex + 1}-{chunk.EndPageIndex + 1}";
        uploadFiles.Add(new ChunkUploadFile(chunk,
            new MinerUUploadRequest(path, Path.GetFileName(path), size, dataId)));
    }

    private static string BuildChunkFileName(string originalFileName, MinerUPdfChunk chunk)
    {
        string stem = Path.GetFileNameWithoutExtension(originalFileName);
        string ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".pdf";
        }

        return $"{stem}.part-{chunk.StartPageIndex + 1:D5}-{chunk.EndPageIndex + 1:D5}{ext}";
    }

    private static string BuildRangeFileName(string originalFileName, OcrPageRange range)
    {
        string stem = Path.GetFileNameWithoutExtension(originalFileName);
        string ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(ext))
        {
            ext = ".pdf";
        }

        return $"{stem}.pages-{range.StartPageIndex + 1:D5}-{range.StartPageIndex + range.PageCount:D5}{ext}";
    }

    private static Result<string> MergeResultZips(
        IReadOnlyList<(int PageIndexOffset, MinerUDownloadedResult Download)> downloads, string downloadDirectory)
    {
        try
        {
            Directory.CreateDirectory(downloadDirectory);
            string mergedPath = Path.Combine(downloadDirectory, $"mineru-merged-{Guid.NewGuid():N}.zip");
            JsonArray contentItems = new();
            JsonArray contentListV2Pages = new();
            StringBuilder markdown = new();

            foreach ((int pageIndexOffset, MinerUDownloadedResult download) in downloads)
            {
                using MinerUZipReader reader = MinerUZipReader.Open(download.ZipPath);
                string? contentListJson = reader.ReadFileContent("_content_list.json");
                if (!string.IsNullOrWhiteSpace(contentListJson))
                {
                    AppendShiftedContentItems(contentItems, contentListJson, pageIndexOffset);
                }

                string? contentListV2Json = reader.ReadFileContent("_content_list_v2.json");
                if (!string.IsNullOrWhiteSpace(contentListV2Json))
                {
                    AppendContentListV2Pages(contentListV2Pages, contentListV2Json, pageIndexOffset);
                }

                string? md = reader.ReadFileContent("full.md");
                if (!string.IsNullOrWhiteSpace(md))
                {
                    if (markdown.Length > 0)
                    {
                        markdown.AppendLine().AppendLine();
                    }

                    markdown.Append(md.Trim());
                }
            }

            using ZipArchive archive = ZipFile.Open(mergedPath, ZipArchiveMode.Create);
            if (contentItems.Count > 0)
            {
                ZipArchiveEntry entry = archive.CreateEntry("merged_content_list.json");
                using StreamWriter writer = new(entry.Open(), Encoding.UTF8);
                writer.Write(contentItems.ToJsonString());
            }

            if (contentListV2Pages.Count > 0)
            {
                ZipArchiveEntry entry = archive.CreateEntry("merged_content_list_v2.json");
                using StreamWriter writer = new(entry.Open(), Encoding.UTF8);
                writer.Write(contentListV2Pages.ToJsonString());
            }

            if (markdown.Length > 0)
            {
                ZipArchiveEntry entry = archive.CreateEntry("full.md");
                using StreamWriter writer = new(entry.Open(), Encoding.UTF8);
                writer.Write(markdown.ToString());
            }

            return Result<string>.Success(mergedPath);
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex,
                                       "infrastructure.mineru-upload-preparer"))
        {
            return Result<string>.Failure("zip_merge_failed", $"Failed to merge MinerU result zips: {ex.Message}");
        }
    }

    private static void AppendShiftedContentItems(JsonArray target, string contentListJson, int pageIndexOffset)
    {
        JsonNode? root = JsonNode.Parse(contentListJson);
        if (root is JsonArray array)
        {
            foreach (JsonNode? item in array)
            {
                if (item is JsonObject obj)
                {
                    target.Add(ShiftFlatItem(obj, pageIndexOffset));
                }
            }

            return;
        }

        if (root is not JsonObject document || document["pages"] is not JsonArray pages)
        {
            return;
        }

        foreach (JsonNode? pageNode in pages)
        {
            if (pageNode is not JsonObject page || page["blocks"] is not JsonArray blocks)
            {
                continue;
            }

            int pageIndex = ReadInt(page, "page_idx") ?? ReadInt(page, "page_num") - 1 ?? 0;
            foreach (JsonNode? blockNode in blocks)
            {
                if (blockNode is not JsonObject block)
                {
                    continue;
                }

                JsonObject flat = CloneObject(block);
                flat["page_idx"] = pageIndex + pageIndexOffset;
                target.Add(flat);
            }
        }
    }

    private static void AppendContentListV2Pages(JsonArray target, string contentListJson, int pageIndexOffset)
    {
        JsonNode? root = JsonNode.Parse(contentListJson);
        if (root is not JsonArray pages)
        {
            return;
        }

        while (target.Count < pageIndexOffset)
        {
            target.Add(new JsonArray());
        }

        foreach (JsonNode? pageNode in pages)
        {
            if (pageNode is JsonArray page)
            {
                target.Add(CloneArray(page));
            }
        }
    }

    private static JsonObject ShiftFlatItem(JsonObject source, int pageIndexOffset)
    {
        JsonObject clone = CloneObject(source);
        int pageIndex = ReadInt(source, "page_idx") ?? ReadInt(source, "page_num") - 1 ?? 0;
        clone["page_idx"] = pageIndex + pageIndexOffset;
        clone.Remove("page_num");
        return clone;
    }

    private static JsonObject CloneObject(JsonObject source)
    {
        return JsonNode.Parse(source.ToJsonString())!.AsObject();
    }

    private static JsonArray CloneArray(JsonArray source)
    {
        return JsonNode.Parse(source.ToJsonString())!.AsArray();
    }

    private static int? ReadInt(JsonObject source, string propertyName)
    {
        return source.TryGetPropertyValue(propertyName, out JsonNode? value) && value is not null &&
               value.GetValueKind() == System.Text.Json.JsonValueKind.Number && value.GetValue<int>() is var number
            ? number
            : null;
    }

    private sealed record ChunkUploadFile(MinerUPdfChunk Chunk, MinerUUploadRequest Request);
}
