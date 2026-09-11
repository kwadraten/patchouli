using System.Security.Cryptography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;

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

    public NdlLiteModelDownloadService(HttpClient httpClient, string modelsDirectory)
    {
        _httpClient = httpClient;
        _modelsDirectory = modelsDirectory;
    }

    /// <summary>
    /// Test seam for exercising transfer/skip logic with synthetic content that
    /// cannot carry the real upstream SHA256. Production always leaves this enabled.
    /// </summary>
    internal bool VerifyContentHash { get; init; } = true;

    public async Task<Result> DownloadAllAsync(IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_modelsDirectory);

        IReadOnlyList<NdlLiteModelFileEntry> files = NdlLiteModelFiles.Files;
        long totalBytes = files.Sum(static file => file.ExpectedBytes);
        long downloadedBytes = 0;

        foreach (NdlLiteModelFileEntry entry in files)
        {
            string targetPath = NdlLiteModelFiles.GetLocalPath(_modelsDirectory, entry);

            if (await IsVerifiedAsync(targetPath, entry, cancellationToken).ConfigureAwait(false))
            {
                downloadedBytes += entry.ExpectedBytes;
                progress?.Report(totalBytes > 0 ? (double)downloadedBytes / totalBytes : 1.0);
                continue;
            }

            Directory.CreateDirectory(Path.GetDirectoryName(targetPath)!);
            string tempPath = targetPath + ".tmp";

            try
            {
                using HttpResponseMessage response = await _httpClient.GetAsync(
                    entry.DownloadUrl,
                    HttpCompletionOption.ResponseHeadersRead,
                    cancellationToken).ConfigureAwait(false);

                if (!response.IsSuccessStatusCode)
                {
                    return Result.Failure(
                        AppErrorCodes.NetworkError,
                        $"Failed to download {entry.RelativePath}: HTTP {(int)response.StatusCode}.");
                }

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
                    progress?.Report(totalBytes > 0 ? (double)downloadedBytes / totalBytes : 1.0);
                }

                fileStream.Close();

                FileInfo tempInfo = new(tempPath);
                if (tempInfo.Length != entry.ExpectedBytes)
                {
                    TryDelete(tempPath);
                    return Result.Failure(
                        AppErrorCodes.NetworkError,
                        $"Downloaded {entry.RelativePath} size {tempInfo.Length} does not match expected {entry.ExpectedBytes}.");
                }

                if (VerifyContentHash)
                {
                    string hash = await ComputeSha256Async(tempPath, cancellationToken).ConfigureAwait(false);
                    if (!string.Equals(hash, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                    {
                        TryDelete(tempPath);
                        return Result.Failure(
                            AppErrorCodes.NetworkError,
                            $"Downloaded {entry.RelativePath} SHA256 {hash} does not match the pinned {entry.Sha256}.");
                    }
                }

                File.Move(tempPath, targetPath, true);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception exception) when
                (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ndlocr-lite-model-download"))
            {
                TryDelete(tempPath);
                return Result.Failure(
                    AppErrorCodes.NetworkError,
                    $"Failed to download {entry.RelativePath}: {exception.Message}");
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
