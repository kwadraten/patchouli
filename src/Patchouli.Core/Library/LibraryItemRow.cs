using Patchouli.Core.Ids;

namespace Patchouli.Core.Library;

public sealed record LibraryItemRow(
    ItemId ItemId,
    string Title,
    string ItemType,
    string Authors,
    string? Year,
    string? PublicationTitle,
    string? Publisher,
    DocumentInstanceId? DocumentInstanceId,
    string? LinkedFileName,
    string? FileAssetId,
    string SourcePath,
    string CreatedAt,
    int PageCount,
    int SearchUnitCount,
    bool HasOcrText,
    PrimaryDocumentOcrIndexState PrimaryDocumentOcrIndexState,
    string IndexStatus,
    string? DeletedAt = null,
    string? MergedIntoItemId = null,
    IReadOnlyList<string>? Tags = null);

/// <summary>
/// A stable keyset cursor over the first-screen library read model, ordered by
/// <c>created_at desc, item_id desc</c>.
/// </summary>
public sealed record LibraryItemCursor(ItemId ItemId, string CreatedAt);

public sealed record LibraryItemPage(
    IReadOnlyList<LibraryItemRow> Rows,
    LibraryItemCursor? NextCursor,
    bool HasMore);
