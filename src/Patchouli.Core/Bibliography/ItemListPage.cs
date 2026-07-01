namespace Patchouli.Core.Bibliography;

public sealed record ItemListPage(
    IReadOnlyList<ItemMetadata> Items,
    string? NextCursor,
    int TotalCount);
