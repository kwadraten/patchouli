namespace Patchouli.Core.Conflicts;

public static class ConflictCode
{
    public const string SettingsConcurrentChange = "SC-01";
    public const string SettingsMissingMapping = "SC-02";
    public const string SettingsInvalidBinding = "SC-03";
    public const string SettingsUnsafeIncoming = "SC-04";
    public const string SettingsSecretConflict = "SC-05";
    public const string SettingsSchemaMismatch = "SC-06";
    public const string SameIdDifferentContent = "CF-01";
    public const string PrimaryDocumentConflict = "CF-02";
    public const string CredentialNotImported = "CF-03";
    public const string FileRelocationMultipleCandidates = "CF-04";
    public const string SourceFileChangedOrBBoxBasisStale = "CF-05";
    public const string LayoutBBoxOrdinaryOverlap = "CF-06";

    public static bool IsKnown(string value)
    {
        return value is SameIdDifferentContent
            or PrimaryDocumentConflict
            or CredentialNotImported
            or FileRelocationMultipleCandidates
            or SourceFileChangedOrBBoxBasisStale
            or LayoutBBoxOrdinaryOverlap;
    }
}
