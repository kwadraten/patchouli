using Patchouli.Core.Ids;
using Patchouli.Core.Results;

namespace Patchouli.Core.Documents;

public interface IDocumentInstanceService
{
    Task<Result<DocumentInstance>> AttachDocumentInstanceAsync(
        ItemId itemId,
        FileAssetId? fileAssetId,
        string instanceType,
        string? title = null,
        bool makePrimary = false,
        CancellationToken cancellationToken = default);

    Task<Result<DocumentInstance>> GetDocumentInstanceAsync(
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    Task<Result<IReadOnlyList<DocumentInstance>>> ListDocumentInstancesForItemAsync(
        ItemId itemId,
        CancellationToken cancellationToken = default);

    Task<Result> SetPrimaryDocumentInstanceAsync(
        ItemId itemId,
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Permanently deletes the document instance and its entire payload: pages, page maps,
    /// document boxes, committed and working tree revisions, document commits, page
    /// translations, OCR runs/results/adoptions, search units and FTS cache entries. The
    /// source FileAsset is never deleted; the returned removal lists it for GC candidate
    /// scheduling. Refuses <see cref="AppErrorCodes.InvalidState"/> while OCR runs are
    /// pending or running for the instance.
    /// </summary>
    Task<Result<DocumentInstanceRemoval>> RemoveDocumentInstanceAsync(
        ItemId itemId,
        DocumentInstanceId documentInstanceId,
        CancellationToken cancellationToken = default);
}
