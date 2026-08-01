using Patchouli.Core.Results;

namespace Patchouli.Mcp;

/// <summary>
/// Numeric error codes shared by the MCP tools and the patchouli-cli executable.
/// Values follow the PRD error table: 0 OK, 2 INVALID_ARGUMENT, 3 NOT_FOUND,
/// 4 PERMISSION_DENIED, 5 REVISION_CONFLICT, 6 INVALID_CONTENT,
/// 7 RESPONSE_TRUNCATED, 8 UNAVAILABLE, 9 NOT_CITABLE.
/// </summary>
public enum McpErrorCode
{
    Ok = 0,
    InvalidArgument = 2,
    NotFound = 3,
    PermissionDenied = 4,
    RevisionConflict = 5,
    InvalidContent = 6,
    ResponseTruncated = 7,
    Unavailable = 8,
    NotCitable = 9
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
            AppErrorCodes.Conflict => McpErrorCode.RevisionConflict,
            AppErrorCodes.NotCitable => McpErrorCode.NotCitable,
            "general_type_not_renderable" => McpErrorCode.NotCitable,
            AppErrorCodes.BiblatexGeneralExportForbidden => McpErrorCode.PermissionDenied,
            AppErrorCodes.UnsupportedOperation => McpErrorCode.Unavailable,
            _ => McpErrorCode.Unavailable
        };
    }

    /// <summary>Maps domain failures surfaced by write operations (put).</summary>
    public static McpErrorCode ToWriteError(string? errorCode)
    {
        return errorCode switch
        {
            AppErrorCodes.InvalidArgument => McpErrorCode.InvalidArgument,
            AppErrorCodes.NotFound => McpErrorCode.NotFound,
            AppErrorCodes.Conflict => McpErrorCode.RevisionConflict,
            AppErrorCodes.NotCitable => McpErrorCode.NotCitable,
            AppErrorCodes.ValidationFailed => McpErrorCode.InvalidContent,
            AppErrorCodes.UnsupportedOperation => McpErrorCode.PermissionDenied,
            AppErrorCodes.BiblatexParseFailed or AppErrorCodes.BiblatexWriteFailed or
                AppErrorCodes.BiblatexHelperFailed or AppErrorCodes.BiblatexVerifyFailed or
                AppErrorCodes.BiblatexMissingTitle or AppErrorCodes.BiblatexEncodingError =>
                McpErrorCode.InvalidContent,
            _ => McpErrorCode.Unavailable
        };
    }
}
