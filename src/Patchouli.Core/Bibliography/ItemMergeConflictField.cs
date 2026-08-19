namespace Patchouli.Core.Bibliography;

/// <summary>
/// A single field that has conflicting values between the source and target items being merged.
/// </summary>
public sealed record ItemMergeConflictField(
    string FieldName,
    string Label,
    string TargetValue,
    string SourceValue,
    string SelectedValue);
