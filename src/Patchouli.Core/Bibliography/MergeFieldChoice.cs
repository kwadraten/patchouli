namespace Patchouli.Core.Bibliography;

/// <summary>
/// A user decision for a single conflict field during an item merge.
/// </summary>
public sealed record MergeFieldChoice(string FieldName, bool UseSourceValue);
