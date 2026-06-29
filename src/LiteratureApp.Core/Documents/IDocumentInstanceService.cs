using LiteratureApp.Core.Ids;
using LiteratureApp.Core.Results;

namespace LiteratureApp.Core.Documents;

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
}
