using Patchouli.Core.Ids;

namespace Patchouli.Core.Operations;

public sealed record BlockingOperation(
    BlockingOperationId OperationId,
    string OperationType,
    string ScopeType,
    string? ScopeId,
    string Status,
    int? ProgressCurrent,
    int? ProgressTotal,
    string? ProgressLabel,
    bool CanCancel,
    string? FailureCode,
    string? FailureMessage,
    IReadOnlyList<string> NextActions,
    DateTimeOffset CreatedAt,
    DateTimeOffset UpdatedAt);

public static class BlockingOperationStatus
{
    public const string Running = "running";
    public const string Completed = "completed";
    public const string Failed = "failed";
    public const string Cancelled = "cancelled";

    public static bool IsTerminal(string value)
        => value is Completed or Failed or Cancelled;
}

public static class BlockingOperationTypes
{
    public const string InitialRootScan = "initial_root_scan";
    public const string FileSearchRootScan = "file_search_root_scan";
    public const string SnapshotImportValidation = "snapshot_import_validation";
    public const string McpStartValidation = "mcp_start_validation";
    public const string CslStyleInstall = "csl_style_install";
    public const string SearchIndexRebuild = "search_index_rebuild";
}

public static class BlockingOperationScopeTypes
{
    public const string McpServerSettings = "mcp_server_settings";
    public const string CslStyle = "csl_style";
    public const string SnapshotImport = "snapshot_import";
    public const string FileSearchRoot = "file_search_root";
    public const string SearchIndex = "search_index";
}

public static class BlockingOperationLogLevel
{
    public const string Info = "info";
    public const string Warning = "warning";
    public const string Error = "error";
}
