namespace Patchouli.Core.Results;

public static class AppErrorCodes
{
    public const string LibraryMismatch = "library_mismatch";
    public const string NotFound = "not_found";
    public const string InvalidState = "invalid_state";
    public const string InvalidArgument = "invalid_argument";
    public const string ValidationFailed = "validation_failed";
    public const string DatabaseError = "database_error";
    public const string UnsupportedOperation = "unsupported_operation";
    public const string NetworkError = "network_error";
    public const string Conflict = "conflict";
    public const string MappingRequired = "mapping_required";
    public const string StaleSettingsRevision = "stale_settings_revision";
    public const string BiblatexParseFailed = "biblatex_parse_failed";
    public const string BiblatexWriteFailed = "biblatex_write_failed";
    public const string BiblatexHelperFailed = "biblatex_helper_failed";
    public const string BiblatexVerifyFailed = "biblatex_verify_failed";
    public const string BiblatexMissingTitle = "biblatex_missing_title";
    public const string BiblatexEncodingError = "biblatex_encoding_error";
    public const string BiblatexGeneralExportForbidden = "biblatex_general_export_forbidden";
    public const string NotCitable = "not_citable";
    public const string ItemMerged = "item_merged";
    public const string ItemInTrash = "item_in_trash";
}
