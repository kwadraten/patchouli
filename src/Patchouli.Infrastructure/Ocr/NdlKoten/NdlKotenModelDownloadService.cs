using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;

namespace Patchouli.Infrastructure.Ocr.NdlKoten;

public sealed class NdlKotenModelDownloadService : INdlKotenModelDownloadService
{
    private readonly HttpClient _httpClient;
    private readonly string _modelsDirectory;

    public NdlKotenModelDownloadService(HttpClient httpClient, string modelsDirectory)
    {
        _httpClient = httpClient;
        _modelsDirectory = modelsDirectory;
    }

    public async Task<Result> DownloadAllAsync(IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        Directory.CreateDirectory(_modelsDirectory);

        IReadOnlyList<ModelFileEntry> files = NdlKotenModelFiles.Files;
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
                    continue;
                }
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
                    return Result.Failure(
                        AppErrorCodes.NetworkError,
                        $"Downloaded {entry.RelativePath} size {tempInfo.Length} does not match expected {entry.ExpectedBytes}.");
                }

                File.Move(tempPath, targetPath, true);
            }
            catch (OperationCanceledException)
            {
                TryDelete(tempPath);
                throw;
            }
            catch (Exception exception) when
                (UnexpectedExceptionReporter.ReportCatch(exception, "infrastructure.ndl-koten-model-download"))
            {
                TryDelete(tempPath);
                return Result.Failure(
                    AppErrorCodes.NetworkError,
                    $"Failed to download {entry.RelativePath}: {exception.Message}");
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
