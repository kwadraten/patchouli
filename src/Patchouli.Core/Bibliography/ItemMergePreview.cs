using Patchouli.Core.Ids;

namespace Patchouli.Core.Bibliography;

/// <summary>
/// A preview of what will happen when <paramref name="SourceItemId"/> is merged into
/// <paramref name="TargetItemId"/>. Conflicting fields are listed with the default choice
/// (target value when non-empty, otherwise source value).
/// </summary>
public sealed record ItemMergePreview(
    ItemId SourceItemId,
    ItemId TargetItemId,
    string SourceTitle,
    string TargetTitle,
    IReadOnlyList<ItemMergeConflictField> ConflictFields,
    IReadOnlyList<ItemMergeMissingField> MissingFields,
    IReadOnlyList<string> TagUnion,
    int DocumentInstancesToTransfer);
