using Patchouli.Core.Ids;

namespace Patchouli.Core.Library;

/// <summary>
/// The document-specific fields needed when navigating from a document workspace back to its library item.
/// </summary>
public sealed record DocumentNavigationRow(
    ItemId ItemId,
    DocumentInstanceId DocumentInstanceId,
    string? FileAssetId,
    string FileName,
    string SourcePath,
    int PageCount,
    int SearchUnitCount,
    string IndexStatus);
