using System.Security.Cryptography;
using Patchouli.Core.Diagnostics;
using Patchouli.Core.Results;
using Patchouli.Core.Update;

namespace Patchouli.Infrastructure.Update;

/// <summary>Progress snapshot reported while an installer downloads.</summary>
public sealed record UpdateDownloadProgress(long BytesReceived, long? TotalBytes)
{
    /// <summary>0..1 fraction, or null when the total size is unknown.</summary>
    public double? Fraction => TotalBytes is > 0
        ? Math.Clamp((double)BytesReceived / TotalBytes.Value, 0, 1)
        : null;
}

/// <summary>
/// A downloaded installer that passed verification and is safe to hand to the
/// installer launcher. <see cref="Sha256Verified"/> is true when the file was
/// checked against a SHA256 sidecar; otherwise the file was verified by size
/// against the release metadata (see ADR 0048 for the policy). The file keeps
/// the release asset's installer extension (see <see
/// cref="UpdateDownloader.InstallerExtensionFor"/>) so <see cref="UpdateLauncher"/>
/// can dispatch on it.
/// </summary>
public sealed record VerifiedDownload(string FilePath, bool Sha256Verified);

/// <summary>
/// Issue #15: downloads installer assets with progress, cancellation and
/// integrity verification. The installer is streamed to a uniquely-named
/// temporary file; a failed, cancelled or unverified download is deleted and
/// never handed out, so a corrupt or partial installer can never be executed.
/// </summary>
public sealed class UpdateDownloader
{
    private const int BufferSize = 81920;

    /// <summary>
    /// Installer extensions the launcher knows how to start, matched against
    /// the release asset name (see <see cref="UpdateAssetSelector"/>). Anything
    /// else falls back to <c>.tmp</c>, which the launcher's extension guard
    /// rejects before any launch is attempted.
    /// </summary>
    private static readonly string[] KnownInstallerExtensions = [".exe", ".dmg", ".AppImage"];

    /// <summary>
    /// How long a retained installer download ("稍后安装") survives in the temp
    /// directory before <see cref="CleanupStaleDownloads"/> removes it at
    /// startup. Long enough to install manually later; bounded so temp files
    /// cannot accumulate forever.
    /// </summary>
    public static readonly TimeSpan StaleDownloadRetention = TimeSpan.FromDays(7);

    private readonly HttpClient _httpClient;

    public UpdateDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient;
    }

    /// <summary>
    /// Returns the temp-file extension for a release asset: the asset's own
    /// extension when it is a known installer kind, <c>.tmp</c> otherwise.
    /// The launcher dispatches on the file extension, so a hardcoded
    /// <c>.tmp</c> would miss every platform branch (Windows would fail with
    /// ERROR_NO_ASSOCIATION).
    /// </summary>
    internal static string InstallerExtensionFor(string? assetName)
    {
        string extension = Path.GetExtension(assetName ?? string.Empty);
        return KnownInstallerExtensions.Contains(extension, StringComparer.OrdinalIgnoreCase)
            ? extension
            : ".tmp";
    }

    /// <summary>
    /// Deletes installer downloads older than <see cref="StaleDownloadRetention"/>
    /// from the temp directory. Called once at app startup; covers files the
    /// user kept via "稍后安装" as well as installers left behind after a
    /// successful install. Best-effort; never throws.
    /// </summary>
    public static void CleanupStaleDownloads()
    {
        try
        {
            DateTimeOffset cutoff = DateTimeOffset.UtcNow - StaleDownloadRetention;
            foreach (string path in Directory.EnumerateFiles(
                         Path.GetTempPath(), "patchouli-update-*"))
            {
                try
                {
                    if (File.GetLastWriteTimeUtc(path) < cutoff.UtcDateTime)
                    {
                        File.Delete(path);
                    }
                }
                catch (Exception exception) when (exception is IOException
                                                      or UnauthorizedAccessException)
                {
                    _ = exception;
                }
            }
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "update-download", "cleanup-stale"))
        {
            // Best-effort cleanup: Path.GetTempPath() and the directory
            // enumeration above can throw exception types beyond IO/permission
            // failures; report unexpected errors without interrupting startup.
            return;
        }
    }

    public async Task<Result<VerifiedDownload>> DownloadAsync(
        ReleaseAssetInfo asset,
        IProgress<UpdateDownloadProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(asset);
        if (string.IsNullOrWhiteSpace(asset.DownloadUrl))
        {
            return Result<VerifiedDownload>.Failure(AppErrorCodes.ValidationFailed,
                "安装包下载地址为空，无法下载。");
        }

        string temporary = Path.Combine(Path.GetTempPath(),
            $"patchouli-update-{Guid.NewGuid():N}{InstallerExtensionFor(asset.Name)}");
        bool keepFile = false;
        try
        {
            cancellationToken.ThrowIfCancellationRequested();

            using HttpResponseMessage response = await _httpClient.GetAsync(asset.DownloadUrl,
                HttpCompletionOption.ResponseHeadersRead, cancellationToken).ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<VerifiedDownload>.Failure(AppErrorCodes.NetworkError,
                    $"下载安装包失败：HTTP {(int)response.StatusCode}。");
            }

            long? totalBytes = response.Content.Headers.ContentLength is > 0
                ? response.Content.Headers.ContentLength
                : asset.SizeBytes > 0
                    ? asset.SizeBytes
                    : null;

            await using (FileStream destination = new(temporary, FileMode.CreateNew,
                             FileAccess.Write, FileShare.None))
            {
                await using Stream source = await response.Content
                    .ReadAsStreamAsync(cancellationToken).ConfigureAwait(false);
                byte[] buffer = new byte[BufferSize];
                long downloaded = 0;
                int read;
                while ((read = await source.ReadAsync(buffer, cancellationToken)
                           .ConfigureAwait(false)) > 0)
                {
                    await destination.WriteAsync(buffer.AsMemory(0, read), cancellationToken)
                        .ConfigureAwait(false);
                    downloaded += read;
                    if (asset.SizeBytes > 0 && downloaded > asset.SizeBytes)
                    {
                        return Result<VerifiedDownload>.Failure(AppErrorCodes.NetworkError,
                            "下载的安装包超出了发布页标注的大小，下载已中止。");
                    }

                    progress?.Report(new UpdateDownloadProgress(downloaded, totalBytes));
                }

                await destination.FlushAsync(cancellationToken).ConfigureAwait(false);

                if (downloaded == 0)
                {
                    return Result<VerifiedDownload>.Failure(AppErrorCodes.NetworkError,
                        "下载的安装包为空。");
                }

                if (asset.SizeBytes > 0 && downloaded != asset.SizeBytes)
                {
                    return Result<VerifiedDownload>.Failure(AppErrorCodes.NetworkError,
                        "下载的安装包不完整（大小与发布页标注不一致），请重试。");
                }
            }

            Result<bool> integrity = await VerifyIntegrityAsync(asset, temporary, cancellationToken)
                .ConfigureAwait(false);
            if (integrity.IsFailure)
            {
                return Result<VerifiedDownload>.Failure(
                    integrity.ErrorCode ?? AppErrorCodes.NetworkError,
                    integrity.ErrorMessage ?? "安装包完整性校验失败。");
            }

            bool sha256Verified = integrity.Value;
            keepFile = true;
            progress?.Report(new UpdateDownloadProgress(
                new FileInfo(temporary).Length, totalBytes));
            return Result<VerifiedDownload>.Success(new VerifiedDownload(temporary, sha256Verified));
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return Result<VerifiedDownload>.Failure(AppErrorCodes.NetworkError,
                $"下载安装包下载超时：{exception.Message}");
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.update-downloader"))
        {
            return Result<VerifiedDownload>.Failure(AppErrorCodes.NetworkError,
                $"下载安装包失败：{exception.Message}");
        }
        finally
        {
            if (!keepFile)
            {
                TryDelete(temporary);
            }
        }
    }

    /// <summary>
    /// Deletes a downloaded installer when the update dialog is dismissed
    /// without a choice (neither "立即安装" nor "稍后安装"). "稍后安装" keeps
    /// the file for manual installation; kept files that outlive <see
    /// cref="StaleDownloadRetention"/> are removed by <see
    /// cref="CleanupStaleDownloads"/>. Best-effort; never throws.
    /// </summary>
    public static void DeleteDownload(string? path)
    {
        TryDelete(path);
    }

    /// <summary>
    /// Integrity policy (ADR 0048): verify a confirmed sidecar when published.
    /// A confirmed absence may use a positive byte-exact release size; unknown
    /// status and every sidecar retrieval or parse failure reject the download.
    /// </summary>
    private async Task<Result<bool>> VerifyIntegrityAsync(
        ReleaseAssetInfo asset,
        string filePath,
        CancellationToken cancellationToken)
    {
        if (asset.ChecksumStatus == ChecksumSidecarStatus.Unknown)
        {
            return Result<bool>.Failure(AppErrorCodes.NetworkError,
                "无法确认发布页是否提供 SHA256 校验文件，请从 GitHub Release 页面手动下载更新。");
        }

        if (asset.ChecksumStatus == ChecksumSidecarStatus.Absent)
        {
            if (asset.SizeBytes <= 0)
            {
                return Result<bool>.Failure(AppErrorCodes.NetworkError,
                    "发布页未提供 SHA256 校验文件，也未提供有效文件大小，无法验证安装包。");
            }

            return Result<bool>.Success(false);
        }

        Result<string> sidecarResult = await TryDownloadSidecarHashAsync(asset.DownloadUrl, cancellationToken)
            .ConfigureAwait(false);
        if (sidecarResult.IsFailure)
        {
            return Result<bool>.Failure(sidecarResult.ErrorCode ?? AppErrorCodes.NetworkError,
                sidecarResult.ErrorMessage ?? "获取 SHA256 校验文件失败，安装包不会执行。");
        }

        string actual;
        await using (FileStream stream = File.OpenRead(filePath))
        {
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            actual = Convert.ToHexString(hash);
        }

        if (!actual.Equals(sidecarResult.Value, StringComparison.OrdinalIgnoreCase))
        {
            return Result<bool>.Failure(AppErrorCodes.NetworkError,
                "安装包 SHA256 校验失败：文件可能已损坏或被篡改，不会执行该文件。");
        }

        return Result<bool>.Success(true);
    }

    /// <summary>
    /// Fetches and parses a sidecar known to exist from the GitHub release asset
    /// list. Only absence confirmed from that list permits the size fallback.
    /// </summary>
    private async Task<Result<string>> TryDownloadSidecarHashAsync(
        string assetUrl,
        CancellationToken cancellationToken)
    {
        try
        {
            using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(30));
            using CancellationTokenSource linked = CancellationTokenSource
                .CreateLinkedTokenSource(cancellationToken, timeout.Token);
            using HttpResponseMessage response = await _httpClient.GetAsync(
                    assetUrl + ".sha256", HttpCompletionOption.ResponseHeadersRead, linked.Token)
                .ConfigureAwait(false);
            if (!response.IsSuccessStatusCode)
            {
                return Result<string>.Failure(AppErrorCodes.NetworkError,
                    $"获取 SHA256 校验文件失败：HTTP {(int)response.StatusCode}，安装包不会执行。");
            }

            string text = (await response.Content.ReadAsStringAsync(linked.Token)
                .ConfigureAwait(false)).Trim();
            // sha256sum format: "<hex>  <filename>"
            string hex = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
            return hex.Length == 64 && hex.All(static c => Uri.IsHexDigit(c))
                ? Result<string>.Success(hex)
                : Result<string>.Failure(AppErrorCodes.NetworkError,
                    "SHA256 校验文件格式无效，安装包不会执行。");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException exception)
        {
            return Result<string>.Failure(AppErrorCodes.NetworkError,
                $"获取 SHA256 校验文件超时：{exception.Message}，安装包不会执行。");
        }
        catch (Exception exception) when (UnexpectedExceptionReporter.ReportCatch(exception,
                                              "infrastructure.update-downloader-sidecar"))
        {
            return Result<string>.Failure(AppErrorCodes.NetworkError,
                $"获取 SHA256 校验文件失败：{exception.Message}，安装包不会执行。");
        }
    }

    private static void TryDelete(string? path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            return;
        }

        try
        {
            if (File.Exists(path))
            {
                File.Delete(path);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            _ = exception;
        }
    }
}
