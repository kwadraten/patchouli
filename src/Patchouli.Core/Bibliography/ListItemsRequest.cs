namespace Patchouli.Core.Bibliography;

public sealed record ListItemsRequest(
    string? Query = null,
    string? ItemType = null,
    int PageSize = 50,
    string? Cursor = null);
