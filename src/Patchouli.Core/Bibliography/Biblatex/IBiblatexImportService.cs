using Patchouli.Core.Conflicts;
using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Bibliography.Biblatex;

public sealed record BiblatexFileSkip(string Reason, int Count);

public sealed record BiblatexImportApplyResult(
    IReadOnlyList<string> CreatedItemIds,
    IReadOnlyList<string> UpdatedItemIds,
    IReadOnlyList<BiblatexFileSkip> FileSkips,
    string StatusMessage);

public sealed record BiblatexSingleImportPreview(
    BiblatexMappedItem Source,
    IReadOnlyList<BiblatexFieldConflict> FieldConflicts,
    ConflictDescriptor? FieldConflictDescriptor);

public sealed record BiblatexBatchImportPreview(
    BiblatexBatchImportPlan Plan);

public interface IBiblatexImportService
{
    Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseTextAsync(
        string text,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<BiblatexEntryDto>>> ParseFileAsync(
        string path,
        CancellationToken cancellationToken = default);

    Task<Result<BiblatexSingleImportPreview>> PreviewSingleAsync(
        BiblatexEntryDto entry,
        ItemId? targetItemId,
        CancellationToken cancellationToken = default);

    Task<Result<BiblatexImportApplyResult>> ApplySingleAsync(
        BiblatexMappedItem source,
        ItemId? targetItemId,
        IReadOnlyDictionary<string, string>? fieldChoices,
        string? bibFileDirectory,
        CancellationToken cancellationToken = default);

    Task<Result<BiblatexBatchImportPreview>> PreviewBatchAsync(
        IReadOnlyList<BiblatexEntryDto> entries,
        CancellationToken cancellationToken = default);

    Task<Result<BiblatexImportApplyResult>> ApplyBatchAsync(
        BiblatexBatchImportPlan plan,
        IReadOnlyDictionary<string, string>? linkChoices,
        string? bibFileDirectory,
        CancellationToken cancellationToken = default);

    Task<Result<string>> ExportItemsAsync(
        IReadOnlyList<ItemId> itemIds,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Exports a single item as BibLaTeX for the agent (CLI/MCP) surface. Unlike
    /// <see cref="ExportItemsAsync"/>, general items are projected to <c>@misc</c>
    /// so the agent can fetch and round-trip them without promoting or demoting
    /// the underlying Patchouli item type. This path is intentionally separate
    /// from the UI export layer which continues to forbid general export.
    /// </summary>
    Task<Result<string>> ExportItemForAgentAsync(
        ItemId itemId,
        CancellationToken cancellationToken = default);
}
