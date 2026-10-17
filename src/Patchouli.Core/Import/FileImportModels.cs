using Patchouli.Core.Results;

namespace Patchouli.Core.Import;

/// <summary>
/// Issue #11: trigger actions that pick a file handling behavior independently.
/// Each action is configured with its own <c>FileHandlingBehavior</c>; MCP callers
/// cannot override the behavior per request.
/// </summary>
public enum FileImportTrigger
{
    /// <summary>MCP <c>add</c> tool import.</summary>
    McpAdd = 0,

    /// <summary>PDF files dragged onto the desktop window.</summary>
    DragDrop = 1,

    /// <summary>Scan/first-run import keeps the existing behavior contract.</summary>
    Scan = 2
}

/// <summary>Local file kinds the v1 import surface accepts.</summary>
public enum LocalImportKind
{
    Unknown = 0,
    Pdf = 1,
    Csl = 2
}

/// <summary>Request to import one host-accessible local file into the library.</summary>
/// <param name="LocalPath">Absolute or relative host-local file path. URLs are rejected.</param>
/// <param name="Kind">Explicit kind ("pdf" or "csl"); null lets the service content-sniff.</param>
/// <param name="Trigger">Which action entry picked the file handling behavior.</param>
/// <param name="AuthorizeKindAsync">Optional kind-specific authorization gate. When set it runs
/// after kind resolution (which needs only read-only validation) and before any file move or
/// ingestion, so callers cannot reach mutations without the kind's permission.</param>
public sealed record FileImportRequest(
    string LocalPath,
    string? Kind,
    FileImportTrigger Trigger,
    Func<LocalImportKind, Task<Result>>? AuthorizeKindAsync = null);

/// <summary>One non-fatal import note surfaced through unified result reporting.</summary>
public sealed record FileImportIssue(string Code, string Message);

/// <summary>Outcome of one local file import.</summary>
/// <param name="Success">True only when the file was imported or reused.</param>
/// <param name="Kind">The content kind that was actually imported.</param>
/// <param name="Reused">True when the content was already in the library.</param>
/// <param name="Moved">True when the source file was moved to the first file search root.</param>
/// <param name="ItemUri">Canonical item URI for PDF imports, e.g. patchouli://items/{id}.yml.</param>
/// <param name="TextUri">Canonical text URI, e.g. patchouli://texts/{document-instance-id}/.</param>
/// <param name="StyleUri">Canonical style URI for CSL installs, e.g. patchouli://csl-styles/{id}.csl.</param>
/// <param name="PageCount">PDF page count (0 for CSL).</param>
/// <param name="ImportStatus">imported | already_imported | imported_with_page_failures |
/// installed | style_reused | style_conflict.</param>
/// <param name="BodyStatus">ingested | body_readable | full_text_searchable. Importing a PDF
/// reports "ingested" only: import never claims OCR or indexing is done.</param>
/// <param name="Issues">Non-fatal notes (duplicates, moves, recoveries).</param>
/// <param name="ErrorMessage">Failure detail; null on success. Never carries host paths.</param>
public sealed record FileImportResult(
    bool Success,
    LocalImportKind Kind,
    bool Reused,
    bool Moved,
    string? ItemUri,
    string? TextUri,
    string? StyleUri,
    int PageCount,
    string? ImportStatus,
    string BodyStatus,
    IReadOnlyList<FileImportIssue> Issues,
    string? ErrorMessage)
{
    public static FileImportResult Failure(LocalImportKind kind, string errorCode, string errorMessage)
    {
        return new FileImportResult(false, kind, false, false, null, null, null, 0, null, "ingested", [],
            $"{errorCode}: {errorMessage}");
    }
}

/// <summary>Stable error codes for local file import failures.</summary>
public static class FileImportErrorCodes
{
    public const string UrlNotAllowed = "url_not_allowed";
    public const string PathNotFound = "local_path_not_found";
    public const string NotRegularFile = "not_a_regular_file";
    public const string UnreadableFile = "file_not_readable";
    public const string UnsupportedFormat = "unsupported_import_format";
    public const string KindMismatch = "import_kind_mismatch";
    public const string CloudPlaceholderUnavailable = "cloud_placeholder_unavailable";
    public const string NoSearchRoot = "no_file_search_root";
    public const string SearchRootUnavailable = "search_root_unavailable";
    public const string MoveFailed = "file_move_failed";
    public const string MovePartialFailure = "file_move_partial_failure";
    public const string ImportFailed = "import_failed";
    public const string RecoveryNeeded = "import_recovery_needed";
    public const string RecoveryFailed = "import_recovery_failed";
    public const string StyleConflict = "csl_style_conflict";
    public const string StyleInvalid = "csl_style_invalid";
}
