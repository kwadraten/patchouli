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
/// against the release metadata (see ADR 0048 for the policy).
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

    private readonly HttpClient _httpClient;

    public UpdateDownloader(HttpClient httpClient)
    {
        _httpClient = httpClient;
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
            $"patchouli-update-{Guid.NewGuid():N}.tmp");
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
                : asset.SizeBytes > 0 ? asset.SizeBytes : null;

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
        catch (OperationCanceledException)
        {
            throw;
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
    /// Deletes a downloaded installer (e.g. when the user chooses "稍后安装" or
    /// the install launch fails). Best-effort; never throws.
    /// </summary>
    public static void DeleteDownload(string? path)
    {
        TryDelete(path);
    }

    /// <summary>
    /// Integrity policy (ADR 0048): prefer a <c>{asset}.sha256</c> sidecar when
    /// the release publishes one; the file's SHA256 must match. Older releases
    /// have no sidecar — then the byte-exact size match against the release
    /// metadata (checked during download) plus HTTPS transport integrity is the
    /// verification. Returns whether SHA256 verification was performed.
    /// </summary>
    private async Task<Result<bool>> VerifyIntegrityAsync(
        ReleaseAssetInfo asset,
        string filePath,
        CancellationToken cancellationToken)
    {
        string? expected = await TryDownloadSidecarHashAsync(asset.DownloadUrl, cancellationToken)
            .ConfigureAwait(false);
        if (expected is null)
        {
            // No sidecar: size was already verified byte-exact against the release
            // metadata above; transport integrity comes from HTTPS.
            return Result<bool>.Success(false);
        }

        string actual;
        await using (FileStream stream = File.OpenRead(filePath))
        {
            byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
            actual = Convert.ToHexString(hash);
        }

        if (!actual.Equals(expected, StringComparison.OrdinalIgnoreCase))
        {
            return Result<bool>.Failure(AppErrorCodes.NetworkError,
                "安装包 SHA256 校验失败：文件可能已损坏或被篡改，不会执行该文件。");
        }

        return Result<bool>.Success(true);
    }

    /// <summary>
    /// Fetches the <c>{asset}.sha256</c> sidecar published next to the asset
    /// (see <c>.github/workflows/release.yml</c>). Returns the expected hex
    /// digest, or null when the release has no sidecar. Any failure here is
    /// non-fatal: the caller falls back to size verification.
    /// </summary>
    private async Task<string?> TryDownloadSidecarHashAsync(
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
                return null;
            }

            string text = (await response.Content.ReadAsStringAsync(linked.Token)
                .ConfigureAwait(false)).Trim();
            // sha256sum format: "<hex>  <filename>"
            string hex = text.Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
                .FirstOrDefault() ?? string.Empty;
            return hex.Length == 64 && hex.All(static c => Uri.IsHexDigit(c)) ? hex : null;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            // The caller cancelled: propagate. A local timeout below is
            // non-fatal and falls through to the size-verification fallback.
            throw;
        }
        catch (Exception)
        {
            return null;
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
