namespace Patchouli.Core.Conflicts;

public static class ConflictCode
{
    public const string SameIdDifferentContent = "CF-01";
    public const string PrimaryDocumentConflict = "CF-02";
    public const string CredentialNotImported = "CF-03";
    public const string FileRelocationMultipleCandidates = "CF-04";
    public const string SourceFileChangedOrBBoxBasisStale = "CF-05";
    public const string BiblatexItemFieldConflict = "CF-06";
    public const string BiblatexBatchLinkCandidates = "CF-07";

    public static bool IsKnown(string value)
    {
        return value is SameIdDifferentContent
            or PrimaryDocumentConflict
            or CredentialNotImported
            or FileRelocationMultipleCandidates
            or SourceFileChangedOrBBoxBasisStale
            or BiblatexItemFieldConflict
            or BiblatexBatchLinkCandidates;
    }
}
