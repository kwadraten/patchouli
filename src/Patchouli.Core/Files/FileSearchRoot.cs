using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Files;

public static class LogicalRootKinds
{
    public const string SyncRoot = "sync_root";
    public const string FileSearchRoot = "file_search_root";

    public static bool IsKnown(string value)
    {
        return value is SyncRoot or FileSearchRoot;
    }
}

public static class LogicalRootRecoveryActions
{
    public const string ChooseLocalSyncRoot = "choose_local_sync_root";
    public const string BindLocalFileSearchRoot = "bind_local_file_search_root";
}

public sealed record DeviceRootBinding(
    LibraryId LibraryId,
    string RootKind,
    string LogicalRootId,
    string DeviceId,
    string LocalPath,
    string ProviderIdentity,
    bool IsAvailable,
    string? AuthorizationKind,
    byte[]? AuthorizationPayload,
    int? AuthorizationPayloadVersion,
    DateTimeOffset? AuthorizationUpdatedAt,
    DateTimeOffset UpdatedAt);

public interface IDeviceRootBindingStore
{
    Task<Result<string>> GetDeviceIdAsync(CancellationToken cancellationToken = default);

    Task<Result<DeviceRootBinding?>> GetBindingAsync(
        LibraryId libraryId,
        string rootKind,
        string logicalRootId,
        string deviceId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DeviceRootBinding>>> ListBindingsAsync(
        LibraryId? libraryId = null,
        string? rootKind = null,
        string? deviceId = null,
        CancellationToken cancellationToken = default);

    Task<Result<DeviceRootBinding>> SaveBindingAsync(
        DeviceRootBinding binding,
        CancellationToken cancellationToken = default);

    Task<Result> DeleteBindingAsync(
        LibraryId libraryId,
        string rootKind,
        string logicalRootId,
        string deviceId,
        CancellationToken cancellationToken = default);
}

/// <summary>
/// Snapshot-eligible identity for a user-approved search root. Device paths and authorization data belong to
/// <see cref="FileSearchRootDeviceBinding"/> and must never be published.
/// </summary>
public sealed record FileSearchRootDefinition(
    FileSearchRootId RootId,
    LibraryId LibraryId,
    string DisplayName,
    string Purpose,
    bool IsEnabled,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

/// <summary>Device-local resolution of a logical FileSearchRoot.</summary>
public sealed record FileSearchRootDeviceBinding(
    LibraryId LibraryId,
    string RootKind,
    FileSearchRootId RootId,
    string DeviceId,
    string RootPath,
    string ProviderIdentity,
    bool IsAvailable,
    string? AuthorizationKind,
    byte[]? AuthorizationPayload,
    int? AuthorizationPayloadVersion,
    DateTimeOffset? AuthorizationUpdatedAt,
    DateTimeOffset UpdatedAt);

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

    /// <summary>
    /// The path was obtained through the platform folder picker on macOS. The app is not sandboxed,
    /// so no security-scoped bookmark payload is stored; this kind only records provenance.
    /// </summary>
    public const string TccPicker = "tcc_picker";
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

public sealed record FileSearchRootExcludedEntry(string Path, string RelativePath, string Rule);

public sealed record FileSearchRootScanResult(
    IReadOnlyList<Import.PdfCandidate> Candidates,
    IReadOnlyList<FileSearchRootIssue> SkippedDirectories,
    IReadOnlyList<FileSearchRootIssue> SkippedFiles,
    IReadOnlyList<FileSearchRootExcludedEntry> ExcludedEntries,
    string RootStatus,
    string ScanStatus);
