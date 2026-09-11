using System.Security.Cryptography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;

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

    public RapidOcrModelDownloadService(HttpClient httpClient, string modelsDirectory)
    {
        _httpClient = httpClient;
        _modelsDirectory = modelsDirectory;
    }

    public async Task<Result> DownloadAllAsync(IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_modelsDirectory);

        IReadOnlyList<RapidOcrModelFile> files = RapidOcrModelFiles.Files;
        long totalBytes = files.Sum(static file => file.ExpectedBytes);
        long downloadedBytes = 0;

        foreach (RapidOcrModelFile file in files)
        {
            string targetPath = RapidOcrModelFiles.GetLocalPath(_modelsDirectory, file);
            if (File.Exists(targetPath) && new FileInfo(targetPath).Length == file.ExpectedBytes)
            {
                downloadedBytes += file.ExpectedBytes;
                progress?.Report(totalBytes > 0 ? (double)downloadedBytes / totalBytes : 1.0);
                continue;
            }

            string tempPath = targetPath + ".tmp";
            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(
                    file.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return Result.Failure(AppErrorCodes.NetworkError,
                        $"Failed to download {file.FileName}: HTTP {(int)response.StatusCode}.");
                }

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
                        progress?.Report(totalBytes > 0 ? (double)downloadedBytes / totalBytes : 1.0);
                    }
                }

                FileInfo tempInfo = new(tempPath);
                if (tempInfo.Length != file.ExpectedBytes)
                {
                    TryDelete(tempPath);
                    return Result.Failure(AppErrorCodes.NetworkError,
                        $"Downloaded {file.FileName} size {tempInfo.Length} does not match expected {file.ExpectedBytes}.");
                }

                string hash = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
                if (!string.Equals(hash, file.Sha256, StringComparison.OrdinalIgnoreCase))
                {
                    TryDelete(tempPath);
                    return Result.Failure(AppErrorCodes.NetworkError,
                        $"Downloaded {file.FileName} SHA256 {hash} does not match the pinned {file.Sha256}.");
                }

                File.Move(tempPath, targetPath, true);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception exception) when
                (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.rapidocr-model-download"))
            {
                TryDelete(tempPath);
                return Result.Failure(AppErrorCodes.NetworkError,
                    $"Failed to download {file.FileName}: {exception.Message}");
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
