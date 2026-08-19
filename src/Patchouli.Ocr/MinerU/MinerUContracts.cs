using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Ocr.MinerU;

public sealed record MinerUUploadRequest(
    string LocalPath,
    string FileName,
    long FileSize,
    string DataId);

public sealed record MinerUUploadBatch(
    string BatchId,
    IReadOnlyList<MinerUFileUploadUrl> FileUrls);

public sealed record MinerUFileUploadUrl(
    string FileName,
    string UploadUrl,
    string FileId);

public sealed record MinerUPollResult(
    string BatchId,
    string Status,
    string? FullZipUrl,
    string? ErrorMessage);

public sealed record MinerUDownloadedResult(
    string BatchId,
    string ZipPath,
    string Status);

public sealed record MinerUImportRequest(
    string ZipPath,
    string DocumentInstanceId,
    string? LibraryId,
    IReadOnlyList<PageId>? RequestedPageIds = null);

public sealed record MinerUImportResult(
    bool Success,
    string? ErrorMessage,
    IReadOnlyList<string> WorkingTreeRevisionIds,
    int BoxesCreated,
    IReadOnlyList<string> Warnings);

public static class MinerUProviderStatus
{
    public const string NotConfigured = "not_configured";
    public const string UploadUrlFailed = "upload_url_failed";
    public const string UploadFailed = "upload_failed";
    public const string DownloadFailed = "download_failed";
    public const string WaitingFile = "waiting_file";
    public const string Pending = "pending";
    public const string Running = "running";
    public const string Converting = "converting";
    public const string Done = "done";
    public const string Failed = "failed";
    public const string Timeout = "timeout";
}
