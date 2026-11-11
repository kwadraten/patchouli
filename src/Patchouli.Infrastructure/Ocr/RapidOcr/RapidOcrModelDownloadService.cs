using System.Security.Cryptography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Downloads;
using Patchouli.Core.Results;
using Patchouli.Ocr.Engines.RapidOcr;

namespace Patchouli.Infrastructure.Ocr.RapidOcr;

public interface IRapidOcrModelDownloadService
{
    /// <summary>
    /// Downloads every missing or incomplete RapidOCR model into the configured models
    /// directory. Existing files whose size matches are skipped; new downloads are
    /// verified against the pinned SHA256 before the temporary file is renamed.
    /// Progress is reported as a value between 0.0 and 1.0.
    /// </summary>
    Task<Result> DownloadAllAsync(IProgress<double>? progress = null, CancellationToken cancellationToken = default);
}

public sealed class RapidOcrModelDownloadService : IRapidOcrModelDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly string _modelsDirectory;
    private readonly IDownloadTaskRunner? _downloads;

    public RapidOcrModelDownloadService(HttpClient httpClient, string modelsDirectory,
        IDownloadTaskRunner? downloads = null)
    {
        _httpClient = httpClient;
        _modelsDirectory = modelsDirectory;
        _downloads = downloads;
    }

    public Task<Result> DownloadAllAsync(IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<RapidOcrModelFile> manifest = RapidOcrModelFiles.Files;
        if (_downloads is { } runner)
        {
            return runner.RunAsync("RapidOCR 模型",
                (worker, token) => DownloadAllCoreAsync(worker ?? progress, token),
                progress, cancellationToken,
                manifest.Select(file => new DownloadFileProgress(file.FileName, "queued", 0,
                    file.ExpectedBytes)).ToArray());
        }

        return DownloadAllCoreAsync(progress, cancellationToken);
    }

    private async Task<Result> DownloadAllCoreAsync(IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_modelsDirectory);

        IReadOnlyList<RapidOcrModelFile> files = RapidOcrModelFiles.Files;
        IDownloadProgress? rich = progress as IDownloadProgress;
        long totalBytes = files.Sum(static file => file.ExpectedBytes);
        long downloadedBytes = 0;

        foreach (RapidOcrModelFile file in files)
        {
            string targetPath = RapidOcrModelFiles.GetLocalPath(_modelsDirectory, file);
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length == file.ExpectedBytes)
            {
                downloadedBytes += file.ExpectedBytes;
                progress?.Report(totalBytes > 0 ? (double)downloadedBytes / totalBytes : 1.0);
                rich?.ReportFile(new DownloadFileProgress(file.FileName, "succeeded",
                    file.ExpectedBytes, file.ExpectedBytes));
                continue;
            }

            string tempPath = targetPath + ".tmp";
            DownloadFileProgress current = new(file.FileName, "running", 0, file.ExpectedBytes);

            void ReportFile(DownloadFileProgress value)
            {
                current = value;
                rich?.ReportFile(value);
            }

            ReportFile(current);
            try
            {
                using HttpRequestMessage request = new(HttpMethod.Get, file.DownloadUrl);
                // ModelScope rejects downloads with an empty User-Agent with HTTP 403.
                request.Headers.UserAgent.ParseAdd("Patchouli");
                using HttpResponseMessage response = await _httpClient.SendAsync(
                    request,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Result failure = Result.Failure(AppErrorCodes.NetworkError,
                        $"Failed to download {file.FileName}: HTTP {(int)response.StatusCode}.");
                    rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                    return failure;
                }

                long? fileTotal = file.ExpectedBytes > 0 ? file.ExpectedBytes
                    : response.Content.Headers.ContentLength is > 0 ? response.Content.Headers.ContentLength : null;
                long fileBytes = 0;

                await using (FileStream fileStream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None))
                {
                    await using Stream contentStream = await response.Content
                        .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                    byte[] buffer = new byte[81920];
                    int read;
                    while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                               .ConfigureAwait(false)) > 0)
                    {
                        await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                            .ConfigureAwait(false);
                        downloadedBytes += read;
                        fileBytes += read;
                        progress?.Report(totalBytes > 0 ? (double)downloadedBytes / totalBytes : 1.0);
                        ReportFile(new DownloadFileProgress(file.FileName, "running", fileBytes,
                            fileTotal));
                    }
                }

                ReportFile(new DownloadFileProgress(file.FileName, "verifying", fileBytes,
                    fileTotal));
                FileInfo tempInfo = new(tempPath);
                if (tempInfo.Length != file.ExpectedBytes)
                {
                    TryDelete(tempPath);
                    Result failure = Result.Failure(AppErrorCodes.NetworkError,
                        $"Downloaded {file.FileName} size {tempInfo.Length} does not match expected {file.ExpectedBytes}.");
                    rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                    return failure;
                }

                string hash = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tempPath);
                    Result failure = Result.Failure(AppErrorCodes.NetworkError,
                        $"Downloaded {file.FileName} SHA256 {hash} does not match the pinned {file.Sha256}.");
                    rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                    return failure;
                }

                File.Move(tempPath, targetPath, true);
                rich?.ReportFile(current with { State = "succeeded" });
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                rich?.ReportFile(current with { State = "cancelled" });
                throw;
            }
            catch (Exception exception) when
                (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.rapidocr-model-download"))
            {
                TryDelete(tempPath);
                Result failure = Result.Failure(AppErrorCodes.NetworkError,
                    $"Failed to download {file.FileName}: {exception.Message}");
                rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                return failure;
            }
        }

        return Result.Success();
    }

    private static async Task<string> ComputeSha256Async(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = new(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexStringLower(hash);
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (IOException)
        {
            // Best-effort cleanup; a leftover temporary file must not fail the download.
        }
        catch (UnauthorizedAccessException)
        {
            // Best-effort cleanup; a leftover temporary file must not fail the download.
        }
    }
}
