using System.Security.Cryptography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Downloads;
using Patchouli.Core.Results;
using Patchouli.Ocr.Engines.NdlLite;

namespace Patchouli.Infrastructure.Ocr.NdlLite;

/// <summary>
/// Streams the official NDLOCR-Lite manifest files from the pinned upstream commit.
/// Every file is verified against the pinned byte length and SHA256 before it is
/// promoted, and already-present files are re-hashed so a corrupted same-size file
/// is re-downloaded instead of being trusted.
/// </summary>
public sealed class NdlLiteModelDownloadService : INdlLiteModelDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly string _modelsDirectory;
    private readonly IDownloadTaskRunner? _downloads;

    public NdlLiteModelDownloadService(HttpClient httpClient, string modelsDirectory,
        IDownloadTaskRunner? downloads = null)
    {
        _httpClient = httpClient;
        _modelsDirectory = modelsDirectory;
        _downloads = downloads;
    }

    /// <summary>
    /// Test seam for exercising transfer/skip logic with synthetic content that
    /// cannot carry the real upstream SHA256. Production always leaves this enabled.
    /// </summary>
    internal bool VerifyContentHash { get; init; } = true;

    public Task<Result> DownloadAllAsync(IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<NdlLiteModelFileEntry> manifest = NdlLiteModelFiles.Files;
        if (_downloads is { } runner)
        {
            return runner.RunAsync("NDLOCR-Lite 模型",
                (worker, token) => DownloadAllCoreAsync(worker ?? progress, token),
                progress, cancellationToken,
                manifest.Select(file => new DownloadFileProgress(file.RelativePath, "queued", 0,
                    file.ExpectedBytes)).ToArray());
        }

        return DownloadAllCoreAsync(progress, cancellationToken);
    }

    private async Task<Result> DownloadAllCoreAsync(IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        Directory.CreateDirectory(_modelsDirectory);

        IReadOnlyList<NdlLiteModelFileEntry> files = NdlLiteModelFiles.Files;
        IDownloadProgress? rich = progress as IDownloadProgress;
        long totalBytes = files.Sum(static file => file.ExpectedBytes);
        long downloadedBytes = 0;

        foreach (NdlLiteModelFileEntry entry in files)
        {
            string targetPath = NdlLiteModelFiles.GetLocalPath(_modelsDirectory, entry);

            if (await IsVerifiedAsync(targetPath, entry, cancellationToken).ConfigureAwait(false))
            {
                downloadedBytes += entry.ExpectedBytes;
                progress?.Report(totalBytes > 0 ? (double)downloadedBytes / totalBytes : 1.0);
                rich?.ReportFile(new DownloadFileProgress(entry.RelativePath, "succeeded",
                    entry.ExpectedBytes, entry.ExpectedBytes));
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            string tempPath = targetPath + ".tmp";
            DownloadFileProgress current = new(entry.RelativePath, "running", 0, entry.ExpectedBytes);

            void ReportFile(DownloadFileProgress value)
            {
                current = value;
                rich?.ReportFile(value);
            }

            ReportFile(current);
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(
                    entry.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    Result failure = Result.Failure(
                        AppErrorCodes.NetworkError,
                        $"Failed to download {entry.RelativePath}: HTTP {(int)response.StatusCode}.");
                    rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                    return failure;
                }

                long? fileTotal = entry.ExpectedBytes > 0 ? entry.ExpectedBytes
                    : response.Content.Headers.ContentLength is > 0 ? response.Content.Headers.ContentLength : null;
                long fileBytes = 0;
                await using FileStream fileStream = new(tempPath, FileMode.Create, FileAccess.Write, FileShare.None);
                await using Stream contentStream = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);

                byte[] buffer = new byte[81920];
                int read;
                while ((read = await contentStream.ReadAsync(buffer.AsMemory(0, buffer.Length), cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    await fileStream.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloadedBytes += read;
                    fileBytes += read;
                    progress?.Report(totalBytes > 0 ? (double)downloadedBytes / totalBytes : 1.0);
                    ReportFile(new DownloadFileProgress(entry.RelativePath, "running", fileBytes,
                        fileTotal));
                }

                fileStream.Close();

                ReportFile(new DownloadFileProgress(entry.RelativePath, "verifying", fileBytes,
                    fileTotal));
                FileInfo tempInfo = new(tempPath);
                if (tempInfo.Length != entry.ExpectedBytes)
                {
                    TryDelete(tempPath);
                    Result failure = Result.Failure(
                        AppErrorCodes.NetworkError,
                        $"Downloaded {entry.RelativePath} size {tempInfo.Length} does not match expected {entry.ExpectedBytes}.");
                    rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                    return failure;
                }

                if (VerifyContentHash)
                {
                    string hash = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(tempPath);
                        Result failure = Result.Failure(
                            AppErrorCodes.NetworkError,
                            $"Downloaded {entry.RelativePath} SHA256 {hash} does not match the pinned {entry.Sha256}.");
                        rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                        return failure;
                    }
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
                (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ndlocr-lite-model-download"))
            {
                TryDelete(tempPath);
                Result failure = Result.Failure(
                    AppErrorCodes.NetworkError,
                    $"Failed to download {entry.RelativePath}: {exception.Message}");
                rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                return failure;
            }
        }

        return Result.Success();
    }

    private async Task<bool> IsVerifiedAsync(string path, NdlLiteModelFileEntry entry,
        CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != entry.ExpectedBytes)
        {
            return false;
        }

        if (!VerifyContentHash)
        {
            return true;
        }

        string hash = await ComputeSha256Async(path, cancellationToken).ConfigureAwait(false);
        return string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase);
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
