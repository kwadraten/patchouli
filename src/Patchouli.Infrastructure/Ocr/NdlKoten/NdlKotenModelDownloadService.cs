using Patchouli.Core.Diagnostics;
using Patchouli.Core.Downloads;
using Patchouli.Core.Results;
using Patchouli.Ocr.Engines.NdlKoten;

namespace Patchouli.Infrastructure.Ocr.NdlKoten;

public sealed class NdlKotenModelDownloadService : INdlKotenModelDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly string _modelsDirectory;
    private readonly IDownloadTaskRunner? _downloads;

    public NdlKotenModelDownloadService(HttpClient httpClient, string modelsDirectory,
        IDownloadTaskRunner? downloads = null)
    {
        _httpClient = httpClient;
        _modelsDirectory = modelsDirectory;
        _downloads = downloads;
    }

    public Task<Result> DownloadAllAsync(IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        IReadOnlyList<ModelFileEntry> manifest = NdlKotenModelFiles.Files;
        if (_downloads is { } runner)
        {
            return runner.RunAsync("NDL koten 模型",
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

        IReadOnlyList<ModelFileEntry> files = NdlKotenModelFiles.Files;
        IDownloadProgress? rich = progress as IDownloadProgress;
        long totalBytes = files.Sum(static f => f.ExpectedBytes);
        long downloadedBytes = 0;

        for (int index = 0; index < files.Count; index++)
        {
            ModelFileEntry entry = files[index];
            string targetPath = NdlKotenModelFiles.GetLocalPath(_modelsDirectory, entry);

            if (File.Exists(targetPath))
            {
                FileInfo existing = new(targetPath);
                if (existing.Length == entry.ExpectedBytes)
                {
                    downloadedBytes += entry.ExpectedBytes;
                    progress?.Report(totalBytes > 0 ? (double)downloadedBytes / totalBytes : 1.0);
                    rich?.ReportFile(new DownloadFileProgress(entry.RelativePath, "succeeded",
                        entry.ExpectedBytes, entry.ExpectedBytes));
                    continue;
                }
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
                    Result failure = Result.Failure(
                        AppErrorCodes.NetworkError,
                        $"Downloaded {entry.RelativePath} size {tempInfo.Length} does not match expected {entry.ExpectedBytes}.");
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
                (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ndl-koten-model-download"))
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

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch
        {
            // best-effort cleanup
        }
    }
}
