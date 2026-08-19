namespace Patchouli.Core.Bibliography;

/// <summary>
/// A field that is empty on the target item and can be filled from the source item.
/// </summary>
public sealed record ItemMergeMissingField(string FieldName, string Label, string SourceValue);
