namespace Patchouli.Core.Results;

public static class AppErrorCodes
{
    public const string LibraryMismatch = "library_mismatch";
    public const string NotFound = "not_found";
    public const string InvalidState = "invalid_state";
    public const string ValidationFailed = "validation_failed";
    public const string InvalidEvidenceReference = "invalid_evidence_reference";
    public const string InvalidEvref = "invalid_evref";
    public const string UnsupportedEvrefTarget = "unsupported_evref_target";
    public const string EvidenceResourceMismatch = "evidence_resource_mismatch";
    public const string EvidenceLibraryMismatch = "evidence_library_mismatch";
    public const string EvidenceUnavailable = "evidence_unavailable";
    public const string DatabaseError = "database_error";
    public const string UnsupportedOperation = "unsupported_operation";
    public const string Conflict = "conflict";
    public const string BiblatexParseFailed = "biblatex_parse_failed";
    public const string BiblatexWriteFailed = "biblatex_write_failed";
    public const string BiblatexHelperFailed = "biblatex_helper_failed";
    public const string BiblatexVerifyFailed = "biblatex_verify_failed";
    public const string BiblatexMissingTitle = "biblatex_missing_title";
    public const string BiblatexEncodingError = "biblatex_encoding_error";
    public const string BiblatexGeneralExportForbidden = "biblatex_general_export_forbidden";
}
