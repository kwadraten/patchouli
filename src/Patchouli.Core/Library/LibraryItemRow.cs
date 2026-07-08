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
    int PageCount,
    int SearchUnitCount,
    string OcrStatus,
    string IndexStatus);
