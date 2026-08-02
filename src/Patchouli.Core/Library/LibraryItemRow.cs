using Patchouli.Core.Ids;

namespace Patchouli.Core.Library;

public sealed record LibraryItemRow(
    ItemId ItemId,
    string Title,
    string ItemType,
    string Authors,
    string? Year,
    string? PublicationTitle,
    DocumentInstanceId? DocumentInstanceId,
    string? LinkedFileName,
    string? FileAssetId,
    string SourcePath,
    string CreatedAt,
    int PageCount,
    int SearchUnitCount,
    PrimaryDocumentOcrIndexState PrimaryDocumentOcrIndexState,
    string IndexStatus);

/// <summary>
/// A stable keyset cursor over the first-screen library read model, ordered by
/// <c>created_at desc, item_id desc</c>.
/// </summary>
public sealed record LibraryItemCursor(ItemId ItemId, string CreatedAt);

public sealed record LibraryItemPage(
    IReadOnlyList<LibraryItemRow> Rows,
    LibraryItemCursor? NextCursor,
    bool HasMore);
