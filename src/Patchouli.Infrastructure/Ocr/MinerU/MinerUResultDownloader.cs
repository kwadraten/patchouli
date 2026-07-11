using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using MuPDF.NET;
using Patchouli.Core.Results;
using Patchouli.Infrastructure.Hashing;
using Patchouli.Ocr.MinerU;

namespace Patchouli.Infrastructure.Ocr.MinerU;

public sealed record MinerUUploadLimits(int MaxPagesPerFile, long MaxBytesPerFile, long TargetBytesPerFile)
{
    public const int OfficialMaxPagesPerFile = 200;
    public const long OfficialMaxBytesPerFile = 200L * 1024 * 1024;
    public static MinerUUploadLimits Default { get; } = new(OfficialMaxPagesPerFile, OfficialMaxBytesPerFile, 190L * 1024 * 1024);

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
    public static IReadOnlyList<MinerUPdfChunk> PlanChunks(int pageCount, long sourceSizeBytes, MinerUUploadLimits? limits = null)
    {
        if (pageCount <= 0) return [];
        limits ??= MinerUUploadLimits.Default;
        var estimatedBytesPerPage = Math.Max(1, (long)Math.Ceiling(sourceSizeBytes / (double)pageCount));
        var pagesBySize = Math.Max(1, (int)Math.Floor(limits.TargetBytesPerFile / (double)estimatedBytesPerPage));
        var maxPages = Math.Max(1, Math.Min(limits.MaxPagesPerFile, pagesBySize));
        var chunks = new List<MinerUPdfChunk>();
        for (var start = 0; start < pageCount; start += maxPages)
        {
            var count = Math.Min(maxPages, pageCount - start);
            chunks.Add(new MinerUPdfChunk(start, count, estimatedBytesPerPage * count));
        }
        return chunks;
    }
}

public sealed class MinerUResultDownloader
{
    private readonly IMinerUClient _client;
    private readonly MinerUUploadLimits _limits;

    public MinerUResultDownloader(IMinerUClient client, MinerUUploadLimits? limits = null)
    {
        _client = client;
        _limits = limits ?? MinerUUploadLimits.Default;
    }

    public async Task<Result<MinerUDownloadedResult>> UploadAndExtractAsync(
        string pdfPath,
        string downloadDirectory,
        CancellationToken cancellationToken = default)
    {
        var fileInfo = new System.IO.FileInfo(pdfPath);
        if (!fileInfo.Exists)
            return Result<MinerUDownloadedResult>.Failure("file_not_found", "PDF file was not found.");

        var dataId = await Blake3Hash.ComputeFileAsync(pdfPath, cancellationToken);
        var pageCount = GetPageCount(pdfPath);
        if (pageCount is null)
        {
            if (fileInfo.Length > _limits.MaxBytesPerFile)
                return Result<MinerUDownloadedResult>.Failure("pdf_page_count_unavailable", "PDF exceeds MinerU's file-size limit and page count could not be read for safe splitting.");

            var uploadRequest = new MinerUUploadRequest(pdfPath, fileInfo.Name, fileInfo.Length, dataId);
            return await UploadSingleAsync(uploadRequest, downloadDirectory, cancellationToken);
        }

        var chunks = MinerUPdfChunkPlanner.PlanChunks(pageCount.Value, fileInfo.Length, _limits);
        if (chunks.Count <= 1 && fileInfo.Length <= _limits.MaxBytesPerFile && (pageCount <= _limits.MaxPagesPerFile))
        {
            var uploadRequest = new MinerUUploadRequest(pdfPath, fileInfo.Name, fileInfo.Length, dataId);
            return await UploadSingleAsync(uploadRequest, downloadDirectory, cancellationToken);
        }

        var chunkDirectory = Path.Combine(downloadDirectory, "mineru-upload-chunks", Path.GetFileNameWithoutExtension(pdfPath) + "-" + dataId[..12]);
        var chunkFiles = await CreateChunkFilesAsync(pdfPath, fileInfo.Name, dataId, chunks, chunkDirectory, cancellationToken);
        if (chunkFiles.IsFailure)
            return Result<MinerUDownloadedResult>.Failure(chunkFiles.ErrorCode!, chunkFiles.ErrorMessage!);

        var downloads = new List<(MinerUPdfChunk Chunk, MinerUDownloadedResult Download)>();
        foreach (var chunkFile in chunkFiles.Value)
        {
            var result = await UploadSingleAsync(chunkFile.Request, Path.Combine(downloadDirectory, "mineru-results"), cancellationToken);
            if (result.IsFailure)
                return Result<MinerUDownloadedResult>.Failure(result.ErrorCode!, result.ErrorMessage!);
            downloads.Add((chunkFile.Chunk, result.Value));
        }

        if (downloads.Count == 1)
            return Result<MinerUDownloadedResult>.Success(downloads[0].Download);

        var merged = MergeResultZips(downloads, downloadDirectory);
        return merged.IsSuccess
            ? Result<MinerUDownloadedResult>.Success(new MinerUDownloadedResult("merged-" + dataId[..12], merged.Value, MinerUProviderStatus.Done))
            : Result<MinerUDownloadedResult>.Failure(merged.ErrorCode!, merged.ErrorMessage!);
    }

    private async Task<Result<MinerUDownloadedResult>> UploadSingleAsync(
        MinerUUploadRequest uploadRequest,
        string downloadDirectory,
        CancellationToken cancellationToken)
    {
        var urlResult = await _client.RequestUploadUrlsAsync([uploadRequest], cancellationToken);
        if (urlResult.IsFailure)
            return Result<MinerUDownloadedResult>.Failure(urlResult.ErrorCode!, urlResult.ErrorMessage!);

        var batch = urlResult.Value;
        var fileUrl = batch.FileUrls[0];

        var uploadResult = await _client.UploadFileAsync(fileUrl.UploadUrl, uploadRequest.LocalPath, cancellationToken);
        if (uploadResult.IsFailure)
            return Result<MinerUDownloadedResult>.Failure(uploadResult.ErrorCode!, uploadResult.ErrorMessage!);

        return await _client.WaitForCompletionAndDownloadAsync(batch.BatchId, downloadDirectory, cancellationToken);
    }

    private static int? GetPageCount(string pdfPath)
    {
        try
        {
            using var document = new Document(pdfPath);
            return document.PageCount > 0 ? document.PageCount : null;
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
            var uploadFiles = new List<ChunkUploadFile>();
            using var source = new Document(pdfPath);
            foreach (var chunk in plannedChunks)
                await WriteChunkRecursiveAsync(source, originalFileName, sourceDataId, chunk, chunkDirectory, uploadFiles, cancellationToken);
            return Result<IReadOnlyList<ChunkUploadFile>>.Success(uploadFiles);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mineru-result-downloader"))
        {
            return Result<IReadOnlyList<ChunkUploadFile>>.Failure("pdf_split_failed", $"Failed to split PDF for MinerU upload: {ex.Message}");
        }
    }

    private async Task WriteChunkRecursiveAsync(
        Document source,
        string originalFileName,
        string sourceDataId,
        MinerUPdfChunk chunk,
        string chunkDirectory,
        List<ChunkUploadFile> uploadFiles,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = Path.Combine(chunkDirectory, BuildChunkFileName(originalFileName, chunk));
        using (var output = new Document())
        {
            output.InsertPdf(source, fromPage: chunk.StartPageIndex, toPage: chunk.EndPageIndex);
            output.EzSave(path);
        }

        var size = new System.IO.FileInfo(path).Length;
        if (size > _limits.MaxBytesPerFile)
        {
            if (chunk.PageCount <= 1)
                throw new InvalidOperationException($"A single PDF page exceeds MinerU's {MinerUUploadLimits.OfficialMaxBytesPerFile / (1024 * 1024)}MB upload limit.");

            File.Delete(path);
            var leftCount = chunk.PageCount / 2;
            var rightCount = chunk.PageCount - leftCount;
            await WriteChunkRecursiveAsync(source, originalFileName, sourceDataId, chunk with { PageCount = leftCount }, chunkDirectory, uploadFiles, cancellationToken);
            await WriteChunkRecursiveAsync(source, originalFileName, sourceDataId, new MinerUPdfChunk(chunk.StartPageIndex + leftCount, rightCount, chunk.EstimatedBytes / 2), chunkDirectory, uploadFiles, cancellationToken);
            return;
        }

        var dataId = $"{sourceDataId[..Math.Min(32, sourceDataId.Length)]}-{chunk.StartPageIndex + 1}-{chunk.EndPageIndex + 1}";
        uploadFiles.Add(new ChunkUploadFile(chunk, new MinerUUploadRequest(path, Path.GetFileName(path), size, dataId)));
    }

    private static string BuildChunkFileName(string originalFileName, MinerUPdfChunk chunk)
    {
        var stem = Path.GetFileNameWithoutExtension(originalFileName);
        var ext = Path.GetExtension(originalFileName);
        if (string.IsNullOrWhiteSpace(ext)) ext = ".pdf";
        return $"{stem}.part-{chunk.StartPageIndex + 1:D5}-{chunk.EndPageIndex + 1:D5}{ext}";
    }

    private static Result<string> MergeResultZips(IReadOnlyList<(MinerUPdfChunk Chunk, MinerUDownloadedResult Download)> downloads, string downloadDirectory)
    {
        try
        {
            Directory.CreateDirectory(downloadDirectory);
            var mergedPath = Path.Combine(downloadDirectory, $"mineru-merged-{Guid.NewGuid():N}.zip");
            var contentItems = new JsonArray();
            var contentListV2Pages = new JsonArray();
            var markdown = new StringBuilder();

            foreach (var (chunk, download) in downloads)
            {
                using var reader = MinerUZipReader.Open(download.ZipPath);
                var contentListJson = reader.ReadFileContent("_content_list.json");
                if (!string.IsNullOrWhiteSpace(contentListJson))
                    AppendShiftedContentItems(contentItems, contentListJson, chunk.StartPageIndex);

                var contentListV2Json = reader.ReadFileContent("_content_list_v2.json");
                if (!string.IsNullOrWhiteSpace(contentListV2Json))
                    AppendContentListV2Pages(contentListV2Pages, contentListV2Json);

                var md = reader.ReadFileContent("full.md");
                if (!string.IsNullOrWhiteSpace(md))
                {
                    if (markdown.Length > 0) markdown.AppendLine().AppendLine();
                    markdown.Append(md.Trim());
                }
            }

            using var archive = ZipFile.Open(mergedPath, ZipArchiveMode.Create);
            if (contentItems.Count > 0)
            {
                var entry = archive.CreateEntry("merged_content_list.json");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(contentItems.ToJsonString());
            }

            if (contentListV2Pages.Count > 0)
            {
                var entry = archive.CreateEntry("merged_content_list_v2.json");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(contentListV2Pages.ToJsonString());
            }

            if (markdown.Length > 0)
            {
                var entry = archive.CreateEntry("full.md");
                using var writer = new StreamWriter(entry.Open(), Encoding.UTF8);
                writer.Write(markdown.ToString());
            }

            return Result<string>.Success(mergedPath);
        }
        catch (Exception ex) when (UnexpectedExceptionReporter.ReportCatch(ex, "infrastructure.mineru-result-downloader"))
        {
            return Result<string>.Failure("zip_merge_failed", $"Failed to merge MinerU result zips: {ex.Message}");
        }
    }

    private static void AppendShiftedContentItems(JsonArray target, string contentListJson, int pageIndexOffset)
    {
        var root = JsonNode.Parse(contentListJson);
        if (root is JsonArray array)
        {
            foreach (var item in array)
                if (item is JsonObject obj)
                    target.Add(ShiftFlatItem(obj, pageIndexOffset));
            return;
        }

        if (root is not JsonObject document || document["pages"] is not JsonArray pages)
            return;

        foreach (var pageNode in pages)
        {
            if (pageNode is not JsonObject page || page["blocks"] is not JsonArray blocks)
                continue;

            var pageIndex = ReadInt(page, "page_idx") ?? (ReadInt(page, "page_num") - 1) ?? 0;
            foreach (var blockNode in blocks)
            {
                if (blockNode is not JsonObject block)
                    continue;

                var flat = CloneObject(block);
                flat["page_idx"] = pageIndex + pageIndexOffset;
                target.Add(flat);
            }
        }
    }

    private static void AppendContentListV2Pages(JsonArray target, string contentListJson)
    {
        var root = JsonNode.Parse(contentListJson);
        if (root is not JsonArray pages)
            return;

        foreach (var pageNode in pages)
            if (pageNode is JsonArray page)
                target.Add(CloneArray(page));
    }

    private static JsonObject ShiftFlatItem(JsonObject source, int pageIndexOffset)
    {
        var clone = CloneObject(source);
        var pageIndex = ReadInt(source, "page_idx") ?? (ReadInt(source, "page_num") - 1) ?? 0;
        clone["page_idx"] = pageIndex + pageIndexOffset;
        clone.Remove("page_num");
        return clone;
    }

    private static JsonObject CloneObject(JsonObject source)
        => JsonNode.Parse(source.ToJsonString())!.AsObject();

    private static JsonArray CloneArray(JsonArray source)
        => JsonNode.Parse(source.ToJsonString())!.AsArray();

    private static int? ReadInt(JsonObject source, string propertyName)
        => source.TryGetPropertyValue(propertyName, out var value) && value is not null && value.GetValueKind() == System.Text.Json.JsonValueKind.Number && value.GetValue<int>() is var number
            ? number
            : null;

    private sealed record ChunkUploadFile(MinerUPdfChunk Chunk, MinerUUploadRequest Request);
}
