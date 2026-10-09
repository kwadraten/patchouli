using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>
/// Numeric error codes shared by the MCP tools and the patchouli-cli executable.
/// Values follow the PRD v3 error table: 0 OK, 1 INTERNAL, 2 INVALID_ARGUMENT,
/// 3 NOT_FOUND, 4 PERMISSION_DENIED, 5 RESERVED (put has no base revision and no
/// revision conflict), 6 INVALID_CONTENT, 7 RESPONSE_TRUNCATED, 8 UNAVAILABLE,
/// 9 NOT_CITABLE, 10 DEADLINE_EXCEEDED, 11 CANCELLED, 12 ITEM_IN_TRASH, 13 ITEM_MERGED,
/// 14 WORKFLOW_NOT_FOUND, 15 SESSION_NOT_FOUND, 16 SESSION_STATE_INVALID, 17 DUPLICATE_MESSAGE.
/// </summary>
public enum McpErrorCode
{
    Ok = 0,
    Internal = 1,
    InvalidArgument = 2,
    NotFound = 3,
    PermissionDenied = 4,
    Reserved = 5,
    InvalidContent = 6,
    ResponseTruncated = 7,
    Unavailable = 8,
    NotCitable = 9,
    DeadlineExceeded = 10,
    Cancelled = 11,
    ItemInTrash = 12,
    ItemMerged = 13,
    WorkflowNotFound = 14,
    SessionNotFound = 15,
    SessionStateInvalid = 16,
    DuplicateMessage = 17
}

/// <summary>Single source of truth mapping domain error codes to the shared numeric codes.</summary>
public static class McpErrorMappings
{
    /// <summary>Maps domain failures surfaced by read operations (find/fetch/cite).</summary>
    public static McpErrorCode ToReadError(string? errorCode)
    {
        return errorCode switch
        {
            AppErrorCodes.InvalidArgument => McpErrorCode.InvalidArgument,
            AppErrorCodes.NotFound => McpErrorCode.NotFound,
            AppErrorCodes.ValidationFailed => McpErrorCode.InvalidArgument,
            AppErrorCodes.LibraryMismatch => McpErrorCode.NotFound,
            AppErrorCodes.Conflict => McpErrorCode.InvalidArgument,
            AppErrorCodes.NotCitable => McpErrorCode.NotCitable,
            "general_type_not_renderable" => McpErrorCode.NotCitable,
            AppErrorCodes.BiblatexGeneralExportForbidden => McpErrorCode.PermissionDenied,
            AppErrorCodes.UnsupportedOperation => McpErrorCode.Unavailable,
            AppErrorCodes.ItemInTrash => McpErrorCode.ItemInTrash,
            AppErrorCodes.ItemMerged => McpErrorCode.ItemMerged,
            AppErrorCodes.DatabaseError or AppErrorCodes.InvalidState or AppErrorCodes.MappingRequired
                or AppErrorCodes.StaleSettingsRevision => McpErrorCode.Internal,
            _ => McpErrorCode.Internal
        };
    }

    /// <summary>Maps domain failures surfaced by write operations (put).</summary>
    public static McpErrorCode ToWriteError(string? errorCode)
    {
        return errorCode switch
        {
            AppErrorCodes.InvalidArgument => McpErrorCode.InvalidArgument,
            AppErrorCodes.NotFound => McpErrorCode.NotFound,
            AppErrorCodes.Conflict => McpErrorCode.InvalidArgument,
            AppErrorCodes.NotCitable => McpErrorCode.NotCitable,
            AppErrorCodes.ValidationFailed => McpErrorCode.InvalidContent,
            AppErrorCodes.UnsupportedOperation or AppErrorCodes.BiblatexGeneralExportForbidden =>
                McpErrorCode.PermissionDenied,
            AppErrorCodes.BiblatexParseFailed or AppErrorCodes.BiblatexWriteFailed or
                AppErrorCodes.BiblatexHelperFailed or AppErrorCodes.BiblatexVerifyFailed or
                AppErrorCodes.BiblatexMissingTitle or AppErrorCodes.BiblatexEncodingError =>
                McpErrorCode.InvalidContent,
            AppErrorCodes.ItemInTrash => McpErrorCode.ItemInTrash,
            AppErrorCodes.ItemMerged => McpErrorCode.ItemMerged,
            AppErrorCodes.DatabaseError or AppErrorCodes.InvalidState or AppErrorCodes.MappingRequired
                or AppErrorCodes.StaleSettingsRevision => McpErrorCode.Internal,
            _ => McpErrorCode.Internal
        };
    }
}
