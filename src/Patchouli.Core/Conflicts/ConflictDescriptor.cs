namespace Patchouli.Core.Conflicts;

public sealed record ConflictDescriptor(
    string ConflictCode,
    string Domain,
    string Severity,
    string ObjectType,
    string ObjectId,
    string Summary,
    string? LocalSnapshot,
    string? IncomingSnapshot,
    IReadOnlyList<ConflictAction> RecommendedActions,
    string? SelectedAction = null,
    string ResolutionStatus = ConflictResolutionStatus.Unresolved);

public static class ConflictDomain
{
    public const string SnapshotSync = "snapshot_sync";
    public const string FileResolution = "file_resolution";
    public const string LayoutEdit = "layout_edit";
}

public static class ConflictSeverity
{
    public const string Blocking = "blocking";
    public const string Warning = "warning";
    public const string Info = "info";
}

public static class ConflictResolutionStatus
{
    public const string Unresolved = "unresolved";
    public const string Resolved = "resolved";
    public const string Ignored = "ignored";
    public const string Superseded = "superseded";
}
