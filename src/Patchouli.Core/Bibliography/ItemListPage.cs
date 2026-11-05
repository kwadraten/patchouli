namespace Patchouli.Core.Bibliography;

/// <summary>TotalCount is null only when the request explicitly skips counting.</summary>
public sealed record ItemListPage(
    IReadOnlyList<ItemMetadata> Items,
    string? NextCursor,
    int? TotalCount = null);
