using System.Security.Cryptography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Downloads;
using Patchouli.Core.Results;
using Patchouli.Llm;

namespace Patchouli.Infrastructure.Ocr;

public sealed record LayoutModelFile(string ModelId, string DownloadUrl, long ExpectedBytes, string Sha256);

/// <summary>Installs the evaluated exports into the same managed directories used by layout inference.</summary>
public sealed class LayoutModelDownloadService(
    HttpClient httpClient,
    Func<string, string> resolveModelPath,
    IDownloadTaskRunner? downloads = null)
{
    public static LayoutModelFile Yolo { get; } = new(OcrLayoutModelIds.DocLayoutYolo,
        "https://huggingface.co/wybxc/DocLayout-YOLO-DocStructBench-onnx/resolve/ee7c3d744e5c47c58e267044ac825f95abe69653/doclayout_yolo_docstructbench_imgsz1024.onnx",
        75_324_598, "fece9af02f618b603ff7921ccec6861d13e7e1f9830e091dfb7e8ad9311e5b21");

    public static LayoutModelFile Paddle { get; } = new(OcrLayoutModelIds.PpDocLayoutV3,
        "https://huggingface.co/PaddlePaddle/PP-DocLayoutV3_onnx/resolve/46bbdf188bb0a772c08aed74882ce7e51a8f1ea6/inference.onnx",
        130_502_049, "45bf71750b00739a41fc209f132eb104a4d6b5bb29483c9078164d8b87cf28ba");

    public Task<Result> DownloadAsync(string modelId, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        return modelId switch
        {
            OcrLayoutModelIds.DocLayoutYolo => DownloadAsync(Yolo, progress, cancellationToken),
            OcrLayoutModelIds.PpDocLayoutV3 => DownloadAsync(Paddle, progress, cancellationToken),
            _ => Task.FromResult(Result.Failure(AppErrorCodes.ValidationFailed, "Unknown layout model."))
        };
    }

    internal Task<Result> DownloadAsync(LayoutModelFile file, IProgress<double>? progress = null,
        CancellationToken cancellationToken = default)
    {
        if (downloads is { } runner)
        {
            return runner.RunAsync(file.ModelId,
                (worker, token) => DownloadCoreAsync(file, worker ?? progress, token),
                progress, cancellationToken,
                [new DownloadFileProgress(file.ModelId, "queued", 0, file.ExpectedBytes)]);
        }

        return DownloadCoreAsync(file, progress, cancellationToken);
    }

    private async Task<Result> DownloadCoreAsync(LayoutModelFile file, IProgress<double>? progress,
        CancellationToken cancellationToken)
    {
        IDownloadProgress? rich = progress as IDownloadProgress;
        DownloadFileProgress current = new(file.ModelId, "running", 0, file.ExpectedBytes);

        void ReportFile(DownloadFileProgress value)
        {
            current = value;
            rich?.ReportFile(value);
        }

        ReportFile(current);
        string target = resolveModelPath(file.ModelId);
        string temporary = target + $".{Guid.NewGuid():N}.tmp";
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            if (await MatchesAsync(target, file, cancellationToken))
            {
                progress?.Report(1);
                ReportFile(current with { State = "succeeded", DownloadedBytes = file.ExpectedBytes });
                return Result.Success();
            }

            using HttpResponseMessage response = await httpClient.GetAsync(file.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                Result failure = Result.Failure(AppErrorCodes.NetworkError,
                    $"Failed to download {file.ModelId}: HTTP {(int)response.StatusCode}.");
                rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                return failure;
            }

            long? total = file.ExpectedBytes > 0 ? file.ExpectedBytes
                : response.Content.Headers.ContentLength is > 0 ? response.Content.Headers.ContentLength : null;
            long downloaded = 0;
            await using (FileStream destination = new(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                await using Stream source = await response.Content.ReadAsStreamAsync(cancellationToken)
                    .ConfigureAwait(false);
                byte[] buffer = new byte[81920];
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken).ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken).ConfigureAwait(false);
                    downloaded += read;
                    if (downloaded > file.ExpectedBytes)
                    {
                        Result failure = Result.Failure(AppErrorCodes.NetworkError,
                            "Downloaded layout model exceeds the expected size.");
                        rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                        return failure;
                    }

                    progress?.Report(Math.Min(.99, (double)downloaded / file.ExpectedBytes));
                    ReportFile(new DownloadFileProgress(file.ModelId, "running", downloaded, total));
                }
            }

            ReportFile(new DownloadFileProgress(file.ModelId, "verifying", downloaded, total));
            if (!await MatchesAsync(temporary, file, cancellationToken))
            {
                Result failure = Result.Failure(AppErrorCodes.NetworkError,
                    "Downloaded layout model failed size or SHA256 verification. Please retry.");
                rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
                return failure;
            }

            File.Move(temporary, target, true);
            progress?.Report(1);
            ReportFile(current with { State = "succeeded" });
            return Result.Success();
        }
        catch (OperationCanceledException)
        {
            rich?.ReportFile(current with { State = "cancelled" });
            throw;
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "ocr.layout-model-download"))
        {
            Result failure = Result.Failure(AppErrorCodes.NetworkError,
                $"Layout model download failed: {exception.Message}");
            rich?.ReportFile(current with { State = "failed", ErrorMessage = failure.ErrorMessage });
            return failure;
        }
        finally
        {
            TryDelete(temporary);
        }
    }

    private static async Task<bool> MatchesAsync(string path, LayoutModelFile file, CancellationToken cancellationToken)
    {
        if (!File.Exists(path) || new FileInfo(path).Length != file.ExpectedBytes)
        {
            return false;
        }

        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return string.Equals(Convert.ToHexStringLower(hash), file.Sha256, StringComparison.OrdinalIgnoreCase);
    }

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (IOException)
        {
            // Cleanup is best effort and must not hide the original download failure.
        }
        catch (UnauthorizedAccessException)
        {
            // The original failure is still returned if temporary-file cleanup is denied.
        }
    }
}
