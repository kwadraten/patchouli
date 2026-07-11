using Patchouli.Core.Ids;

namespace Patchouli.Core.Files;

public sealed record FileSearchRoot(
    FileSearchRootId RootId,
    LibraryId LibraryId,
    string RootPath,
    bool IsAvailable,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt,
    string? AuthorizationKind = null,
    byte[]? AuthorizationPayload = null,
    int? AuthorizationPayloadVersion = null,
    DateTimeOffset? AuthorizationUpdatedAt = null);

public sealed record SelectedFileSearchRoot(
    string DisplayPath,
    string ProviderIdentity,
    string AuthorizationKind,
    byte[]? AuthorizationPayload,
    int? AuthorizationPayloadVersion,
    DateTimeOffset SelectedAt);

public sealed record ResolvedFileSearchRoot(
    string DisplayPath,
    string ResolvedPath,
    string ProviderIdentity,
    string AuthorizationKind,
    IDisposable? AccessLease = null);

public static class FileSearchRootAuthorizationKinds
{
    public const string None = "none";
    public const string SecurityScopedBookmark = "security_scoped_bookmark";
}

public static class FileSearchRootStatuses
{
    public const string Available = "available";
    public const string Offline = "offline";
    public const string AccessDenied = "access_denied";
    public const string AuthorizationRequired = "authorization_required";
    public const string Partial = "partial";
}

public static class FileSearchRootScanStatuses
{
    public const string Complete = "complete";
    public const string Partial = "partial";
    public const string Cancelled = "cancelled";
    public const string Failed = "failed";
}

public sealed record FileSearchRootIssue(string Path, string Code, string Reason);

public sealed record FileSearchRootScanResult(
    IReadOnlyList<Import.PdfCandidate> Candidates,
    IReadOnlyList<FileSearchRootIssue> SkippedDirectories,
    IReadOnlyList<FileSearchRootIssue> SkippedFiles,
    string RootStatus,
    string ScanStatus);
